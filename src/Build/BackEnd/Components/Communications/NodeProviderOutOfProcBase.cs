// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.BackEnd.Logging;

#if NETFRAMEWORK
using Microsoft.Build.Eventing;
using System.Security.Principal;
#endif

using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// Contains the shared pieces of code from NodeProviderOutOfProc
    /// and NodeProviderOutOfProcTaskHost.
    /// </summary>
    internal abstract class NodeProviderOutOfProcBase
    {
        /// <summary>
        /// The maximum number of bytes to write
        /// </summary>
        private const int MaxPacketWriteSize = 1048576;

        /// <summary>
        /// The number of times to retry creating an out-of-proc node.
        /// </summary>
        private const int NodeCreationRetries = 10;

        /// <summary>
        /// The amount of time to wait for an out-of-proc node to spool up before we give up.
        /// </summary>
        private const int TimeoutForNewNodeCreation = 30000;

        /// <summary>
        /// The amount of time to wait for an out-of-proc node to exit.
        /// </summary>
        private const int TimeoutForWaitForExit = 30000;

#if !FEATURE_PIPEOPTIONS_CURRENTUSERONLY
        private static readonly WindowsIdentity s_currentWindowsIdentity = WindowsIdentity.GetCurrent();
#endif
        /// <summary>
        /// The build component host.
        /// </summary>
        private IBuildComponentHost _componentHost;

        /// <summary>
        /// Keeps track of the processes we've already checked for nodes so we don't check them again.
        /// We decided to use ConcurrentDictionary of(string, byte) as common implementation of ConcurrentHashSet.
        /// </summary>
        private readonly ConcurrentDictionary<string, byte /*void*/> _processesToIgnore = new();

        /// <summary>
        /// Delegate used to tell the node provider that a context has been created.
        /// </summary>
        /// <param name="context">The created node context.</param>
        internal delegate void NodeContextCreatedDelegate(NodeContext context);

        /// <summary>
        /// Delegate used to tell the node provider that a context has terminated.
        /// </summary>
        /// <param name="nodeId">The id of the node which terminated.</param>
        internal delegate void NodeContextTerminateDelegate(int nodeId);

        /// <summary>
        /// The build component host.
        /// </summary>
        protected IBuildComponentHost ComponentHost
        {
            get { return _componentHost; }
            set { _componentHost = value; }
        }

        /// <summary>
        /// Sends data to the specified node.
        /// </summary>
        /// <param name="context">The node to which data shall be sent.</param>
        /// <param name="packet">The packet to send.</param>
        protected void SendData(NodeContext context, INodePacket packet)
        {
            ErrorUtilities.VerifyThrowArgumentNull(packet);
            context.SendData(packet);
        }

        /// <summary>
        /// Shuts down all of the connected managed nodes.
        /// </summary>
        /// <param name="contextsToShutDown">List of the contexts to be shut down</param>
        /// <param name="enableReuse">Flag indicating if nodes should prepare for reuse.</param>
        protected void ShutdownConnectedNodes(List<NodeContext> contextsToShutDown, bool enableReuse)
        {
            // Send the build completion message to the nodes, causing them to shutdown or reset.
            _processesToIgnore.Clear();

            // We wait for child nodes to exit to avoid them changing the terminal
            // after this process terminates.
            bool waitForExit = !enableReuse &&
                                !Console.IsInputRedirected &&
                                Traits.Instance.EscapeHatches.EnsureStdOutForChildNodesIsPrimaryStdout;

            Task[] waitForExitTasks = waitForExit && contextsToShutDown.Count > 0 ? new Task[contextsToShutDown.Count] : null;
            int i = 0;
            var loggingService = _componentHost.LoggingService;
            foreach (NodeContext nodeContext in contextsToShutDown)
            {
                if (nodeContext is null)
                {
                    continue;
                }
                nodeContext.SendData(new NodeBuildComplete(enableReuse));
                if (waitForExit)
                {
                    waitForExitTasks[i++] = nodeContext.WaitForExitAsync(loggingService);
                }
            }
            if (waitForExitTasks != null)
            {
                Task.WaitAll(waitForExitTasks);
            }
        }

        /// <summary>
        /// Shuts down all of the managed nodes permanently.
        /// </summary>
        /// <param name="nodeReuse">Whether to reuse the node</param>
        /// <param name="terminateNode">Delegate used to tell the node provider that a context has terminated</param>
        protected void ShutdownAllNodes(bool nodeReuse, NodeContextTerminateDelegate terminateNode)
        {
            // INodePacketFactory
            INodePacketFactory factory = new NodePacketFactory();

            List<Process> nodeProcesses = GetPossibleRunningNodes().nodeProcesses.ToList();

            // Find proper MSBuildTaskHost executable name
            string msbuildtaskhostExeName = NodeProviderOutOfProcTaskHost.TaskHostNameForClr2TaskHost;

            // Search for all instances of msbuildtaskhost process and add them to the process list
            nodeProcesses.AddRange(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(msbuildtaskhostExeName)));

            // For all processes in the list, send signal to terminate if able to connect
            foreach (Process nodeProcess in nodeProcesses)
            {
                // A 2013 comment suggested some nodes take this long to respond, so a smaller timeout would miss nodes.
                int timeout = 30;

                // Attempt to connect to the process with the handshake without low priority.
                using Stream nodeStream = TryConnectToProcess(nodeProcess.Id, timeout, NodeProviderOutOfProc.GetHandshake(nodeReuse, false));
                if (nodeStream != null)
                {
                    ShutdownNode(terminateNode, factory, nodeProcess, nodeStream);
                }
                else
                {
                    // If we couldn't connect attempt to connect to the process with the handshake including low priority.
                    using Stream lowPriorityConnection = TryConnectToProcess(nodeProcess.Id, timeout, NodeProviderOutOfProc.GetHandshake(nodeReuse, true));

                    if (lowPriorityConnection != null)
                    {
                        ShutdownNode(terminateNode, factory, nodeProcess, lowPriorityConnection);
                    }
                }
            }

            static void ShutdownNode(NodeContextTerminateDelegate terminateNode, INodePacketFactory factory, Process nodeProcess, Stream nodeStream)
            {
                // If we're able to connect to such a process, send a packet requesting its termination
                CommunicationsUtilities.Trace("Shutting down node with pid = {0}", nodeProcess.Id);
                NodeContext nodeContext = new NodeContext(0, nodeProcess, nodeStream, factory, terminateNode);
                nodeContext.SendData(new NodeBuildComplete(false /* no node reuse */));
            }
        }

        /// <summary>
        /// Finds or creates a child processes which can act as a node.
        /// </summary>
        protected IList<NodeContext> GetNodes(
            string msbuildLocation,
            string commandLineArgs,
            int nextNodeId,
            INodePacketFactory factory,
            Handshake hostHandshake,
            NodeContextCreatedDelegate createNode,
            NodeContextTerminateDelegate terminateNode,
            int numberOfNodesToCreate)
        {
#if DEBUG
            if (Execution.BuildManager.WaitForDebugger)
            {
                commandLineArgs += " /wfd";
            }
#endif

            if (String.IsNullOrEmpty(msbuildLocation))
            {
                msbuildLocation = _componentHost.BuildParameters.NodeExeLocation;
            }

            if (String.IsNullOrEmpty(msbuildLocation))
            {
                string msbuildExeName = Environment.GetEnvironmentVariable("MSBUILD_EXE_NAME");

                if (!String.IsNullOrEmpty(msbuildExeName))
                {
                    // we assume that MSBUILD_EXE_NAME is, in fact, just the name.
                    msbuildLocation = Path.Combine(msbuildExeName, ".exe");
                }
            }

            // Get all process of possible running node processes for reuse and put them into ConcurrentQueue.
            // Processes from this queue will be concurrently consumed by TryReusePossibleRunningNodes while
            //    trying to connect to them and reuse them. When queue is empty, no process to reuse left
            //    new node process will be started.
            string expectedProcessName = null;
            ConcurrentQueue<Process> possibleRunningNodes = null;
#if FEATURE_NODE_REUSE
            // Try to connect to idle nodes if node reuse is enabled.
            if (_componentHost.BuildParameters.EnableNodeReuse)
            {
                IList<Process> possibleRunningNodesList;
                (expectedProcessName, possibleRunningNodesList) = GetPossibleRunningNodes(msbuildLocation);
                possibleRunningNodes = new ConcurrentQueue<Process>(possibleRunningNodesList);

                if (possibleRunningNodesList.Count > 0)
                {
                    CommunicationsUtilities.Trace("Attempting to connect to {1} existing processes '{0}'...", expectedProcessName, possibleRunningNodesList.Count);
                }
            }
#endif
            ConcurrentQueue<NodeContext> nodeContexts = new();
            ConcurrentQueue<Exception> exceptions = new();
            int currentProcessId = EnvironmentUtilities.CurrentProcessId;
            Parallel.For(nextNodeId, nextNodeId + numberOfNodesToCreate, (nodeId) =>
            {
                try
                {
                    if (!TryReuseAnyFromPossibleRunningNodes(currentProcessId, nodeId) && !StartNewNode(nodeId))
                    {
                        // We were unable to reuse or launch a node.
                        CommunicationsUtilities.Trace("FAILED TO CONNECT TO A CHILD NODE");
                    }
                }
                catch (Exception ex)
                {
                    // It will be rethrown as aggregate exception
                    exceptions.Enqueue(ex);
                }
            });
            if (!exceptions.IsEmpty)
            {
                ErrorUtilities.ThrowInternalError("Cannot acquire required number of nodes.", new AggregateException(exceptions.ToArray()));
            }

            return nodeContexts.ToList();

            bool TryReuseAnyFromPossibleRunningNodes(int currentProcessId, int nodeId)
            {
                while (possibleRunningNodes != null && possibleRunningNodes.TryDequeue(out var nodeToReuse))
                {
                    CommunicationsUtilities.Trace("Trying to connect to existing process {2} with id {1} to establish node {0}...", nodeId, nodeToReuse.Id, nodeToReuse.ProcessName);
                    if (nodeToReuse.Id == currentProcessId)
                    {
                        continue;
                    }

                    // Get the full context of this inspection so that we can always skip this process when we have the same taskhost context
                    string nodeLookupKey = GetProcessesToIgnoreKey(hostHandshake, nodeToReuse.Id);
                    if (_processesToIgnore.ContainsKey(nodeLookupKey))
                    {
                        continue;
                    }

                    // We don't need to check this again
                    _processesToIgnore.TryAdd(nodeLookupKey, default);

                    // Attempt to connect to each process in turn.
                    Stream nodeStream = TryConnectToProcess(nodeToReuse.Id, 0 /* poll, don't wait for connections */, hostHandshake);
                    if (nodeStream != null)
                    {
                        // Connection successful, use this node.
                        CommunicationsUtilities.Trace("Successfully connected to existed node {0} which is PID {1}", nodeId, nodeToReuse.Id);
                        string msg = ResourceUtilities.FormatResourceStringIgnoreCodeAndKeyword("NodeReused", nodeId, nodeToReuse.Id);
                        _componentHost.LoggingService.LogBuildEvent(new BuildMessageEventArgs(msg, null, null, MessageImportance.Low)
                        {
                            BuildEventContext = new BuildEventContext(nodeId, BuildEventContext.InvalidTargetId, BuildEventContext.InvalidProjectContextId, BuildEventContext.InvalidTaskId)
                        });

                        CreateNodeContext(nodeId, nodeToReuse, nodeStream);
                        return true;
                    }
                }

                return false;
            }

            // Create a new node process.
            bool StartNewNode(int nodeId)
            {
                CommunicationsUtilities.Trace("Could not connect to existing process, now creating a process...");

                // We try this in a loop because it is possible that there is another MSBuild multiproc
                // host process running somewhere which is also trying to create nodes right now.  It might
                // find our newly created node and connect to it before we get a chance.
                int retries = NodeCreationRetries;
                while (retries-- > 0)
                {
#if FEATURE_NET35_TASKHOST
                    // We will also check to see if .NET 3.5 is installed in the case where we need to launch a CLR2 OOP TaskHost.
                    // Failure to detect this has been known to stall builds when Windows pops up a related dialog.
                    // It's also a waste of time when we attempt several times to launch multiple MSBuildTaskHost.exe (CLR2 TaskHost)
                    // nodes because we should never be able to connect in this case.
                    string taskHostNameForClr2TaskHost = Path.GetFileNameWithoutExtension(NodeProviderOutOfProcTaskHost.TaskHostNameForClr2TaskHost);
                    if (Path.GetFileNameWithoutExtension(msbuildLocation).Equals(taskHostNameForClr2TaskHost, StringComparison.OrdinalIgnoreCase))
                    {
                        if (FrameworkLocationHelper.GetPathToDotNetFrameworkV35(DotNetFrameworkArchitecture.Current) == null)
                        {
                            CommunicationsUtilities.Trace(
                                "Failed to launch node from {0}. The required .NET Framework v3.5 is not installed or enabled. CommandLine: {1}",
                                msbuildLocation,
                                commandLineArgs);

                            string nodeFailedToLaunchError = ResourceUtilities.GetResourceString("TaskHostNodeFailedToLaunchErrorCodeNet35NotInstalled");
                            throw new NodeFailedToLaunchException(null, nodeFailedToLaunchError);
                        }
                    }
#endif
                    // Create the node process
                    INodeLauncher nodeLauncher = (INodeLauncher)_componentHost.GetComponent(BuildComponentType.NodeLauncher);
                    Process msbuildProcess = nodeLauncher.Start(msbuildLocation, commandLineArgs, nodeId);

                    _processesToIgnore.TryAdd(GetProcessesToIgnoreKey(hostHandshake, msbuildProcess.Id), default);

                    // Note, when running under IMAGEFILEEXECUTIONOPTIONS registry key to debug, the process ID
                    // gotten back from CreateProcess is that of the debugger, which causes this to try to connect
                    // to the debugger process. Instead, use MSBUILDDEBUGONSTART=1

                    // Now try to connect to it.
                    Stream nodeStream = TryConnectToProcess(msbuildProcess.Id, TimeoutForNewNodeCreation, hostHandshake);
                    if (nodeStream != null)
                    {
                        // Connection successful, use this node.
                        CommunicationsUtilities.Trace("Successfully connected to created node {0} which is PID {1}", nodeId, msbuildProcess.Id);

                        CreateNodeContext(nodeId, msbuildProcess, nodeStream);
                        return true;
                    }

                    if (msbuildProcess.HasExited)
                    {
                        if (Traits.Instance.DebugNodeCommunication)
                        {
                            try
                            {
                                CommunicationsUtilities.Trace("Could not connect to node with PID {0}; it has exited with exit code {1}. This can indicate a crash at startup", msbuildProcess.Id, msbuildProcess.ExitCode);
                            }
                            catch (InvalidOperationException)
                            {
                                // This case is common on Windows where we called CreateProcess and the Process object
                                // can't get the exit code.
                                CommunicationsUtilities.Trace("Could not connect to node with PID {0}; it has exited with unknown exit code. This can indicate a crash at startup", msbuildProcess.Id);
                            }
                        }
                    }
                    else
                    {
                        CommunicationsUtilities.Trace("Could not connect to node with PID {0}; it is still running. This can occur when two multiprocess builds run in parallel and the other one 'stole' this node", msbuildProcess.Id);
                    }
                }

                return false;
            }

            void CreateNodeContext(int nodeId, Process nodeToReuse, Stream nodeStream)
            {
                NodeContext nodeContext = new(nodeId, nodeToReuse, nodeStream, factory, terminateNode);
                nodeContexts.Enqueue(nodeContext);
                createNode(nodeContext);
            }
        }

        /// <summary>
        /// Finds processes named after either msbuild or msbuildtaskhost.
        /// </summary>
        /// <param name="msbuildLocation"></param>
        /// <returns>
        /// Item 1 is the name of the process being searched for.
        /// Item 2 is the ConcurrentQueue of ordered processes themselves.
        /// </returns>
        private (string expectedProcessName, IList<Process> nodeProcesses) GetPossibleRunningNodes(string msbuildLocation = null)
        {
            if (String.IsNullOrEmpty(msbuildLocation))
            {
                msbuildLocation = Constants.MSBuildExecutableName;
            }

            var expectedProcessName = Path.GetFileNameWithoutExtension(CurrentHost.GetCurrentHost() ?? msbuildLocation);

            var processes = Process.GetProcessesByName(expectedProcessName);
            Array.Sort(processes, (left, right) => left.Id.CompareTo(right.Id));

            return (expectedProcessName, processes);
        }

        /// <summary>
        /// Generate a string from task host context and the remote process to be used as key to lookup processes we have already
        /// attempted to connect to or are already connected to
        /// </summary>
        private string GetProcessesToIgnoreKey(Handshake hostHandshake, int nodeProcessId)
        {
#if NET
            return string.Create(CultureInfo.InvariantCulture, $"{hostHandshake}|{nodeProcessId}");
#else
            return $"{hostHandshake}|{nodeProcessId.ToString(CultureInfo.InvariantCulture)}";
#endif
        }

#if !FEATURE_PIPEOPTIONS_CURRENTUSERONLY
        // This code needs to be in a separate method so that we don't try (and fail) to load the Windows-only APIs when JIT-ing the code
        //  on non-Windows operating systems
        private static void ValidateRemotePipeSecurityOnWindows(NamedPipeClientStream nodeStream)
        {
            SecurityIdentifier identifier = s_currentWindowsIdentity.Owner;
#if FEATURE_PIPE_SECURITY
            PipeSecurity remoteSecurity = nodeStream.GetAccessControl();
#else
            var remoteSecurity = new PipeSecurity(nodeStream.SafePipeHandle, System.Security.AccessControl.AccessControlSections.Access |
                System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Group);
#endif
            IdentityReference remoteOwner = remoteSecurity.GetOwner(typeof(SecurityIdentifier));
            if (remoteOwner != identifier)
            {
                CommunicationsUtilities.Trace("The remote pipe owner {0} does not match {1}", remoteOwner.Value, identifier.Value);
                throw new UnauthorizedAccessException();
            }
        }
#endif

        /// <summary>
        /// Attempts to connect to the specified process.
        /// </summary>
        private Stream TryConnectToProcess(int nodeProcessId, int timeout, Handshake handshake)
        {
            // Try and connect to the process.
            string pipeName = NamedPipeUtil.GetPlatformSpecificPipeName(nodeProcessId);

#pragma warning disable SA1111, SA1009 // Closing parenthesis should be on line of last parameter
            NamedPipeClientStream nodeStream = new NamedPipeClientStream(
                serverName: ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
#if FEATURE_PIPEOPTIONS_CURRENTUSERONLY
                | PipeOptions.CurrentUserOnly
#endif
            );
#pragma warning restore SA1111, SA1009 // Closing parenthesis should be on line of last parameter
            CommunicationsUtilities.Trace("Attempting connect to PID {0} with pipe {1} with timeout {2} ms", nodeProcessId, pipeName, timeout);

            try
            {
                ConnectToPipeStream(nodeStream, pipeName, handshake, timeout);
                return nodeStream;
            }
            catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
            {
                // Can be:
                // UnauthorizedAccessException -- Couldn't connect, might not be a node.
                // IOException -- Couldn't connect, already in use.
                // TimeoutException -- Couldn't connect, might not be a node.
                // InvalidOperationException – Couldn’t connect, probably a different build
                CommunicationsUtilities.Trace("Failed to connect to pipe {0}. {1}", pipeName, e.Message.TrimEnd());

                // If we don't close any stream, we might hang up the child
                nodeStream?.Dispose();
            }

            return null;
        }

        /// <summary>
        /// Connect to named pipe stream and ensure validate handshake and security.
        /// </summary>
        /// <remarks>
        /// Reused by MSBuild server client <see cref="Microsoft.Build.Experimental.MSBuildClient"/>.
        /// </remarks>
        internal static void ConnectToPipeStream(NamedPipeClientStream nodeStream, string pipeName, Handshake handshake, int timeout)
        {
            nodeStream.Connect(timeout);

#if !FEATURE_PIPEOPTIONS_CURRENTUSERONLY
            if (NativeMethodsShared.IsWindows)
            {
                // Verify that the owner of the pipe is us.  This prevents a security hole where a remote node has
                // been faked up with ACLs that would let us attach to it.  It could then issue fake build requests back to
                // us, potentially causing us to execute builds that do harmful or unexpected things.  The pipe owner can
                // only be set to the user's own SID by a normal, unprivileged process.  The conditions where a faked up
                // remote node could set the owner to something else would also let it change owners on other objects, so
                // this would be a security flaw upstream of us.
                ValidateRemotePipeSecurityOnWindows(nodeStream);
            }
#endif

            HandshakeComponents handshakeComponents = handshake.RetrieveHandshakeComponents();
            foreach (var component in handshakeComponents.EnumerateComponents())
            {
                CommunicationsUtilities.Trace("Writing handshake part {0} ({1}) to pipe {2}", component.Key, component.Value, pipeName);
                nodeStream.WriteIntForHandshake(component.Value);
            }

            // This indicates that we have finished all the parts of our handshake; hopefully the endpoint has as well.
            nodeStream.WriteEndOfHandshakeSignal();

            CommunicationsUtilities.Trace("Reading handshake from pipe {0}", pipeName);

#if NETCOREAPP2_1_OR_GREATER
            nodeStream.ReadEndOfHandshakeSignal(true, timeout);
#else
            nodeStream.ReadEndOfHandshakeSignal(true);
#endif
            // We got a connection.
            CommunicationsUtilities.Trace("Successfully connected to pipe {0}...!", pipeName);
        }

        /// <summary>
        /// Class which wraps up the communications infrastructure for a given node.
        /// </summary>
        internal sealed class NodeContext
        {
            private enum ExitPacketState
            {
                None,
                ExitPacketQueued,
                ExitPacketSent
            }

            // The pipe(s) used to communicate with the node.
            private readonly Stream _pipeStream;

            /// <summary>
            /// The factory used to create packets from data read off the pipe.
            /// </summary>
            private readonly INodePacketFactory _packetFactory;

            /// <summary>
            /// The node id assigned by the node provider.
            /// </summary>
            private readonly int _nodeId;

            /// <summary>
            /// The node process.
            /// </summary>
            private readonly Process _process;

            internal Process Process { get { return _process; } }

            /// <summary>
            /// An array used to store the header byte for each packet when read.
            /// </summary>
            private readonly byte[] _headerByte;

            /// <summary>
            /// A buffer typically big enough to handle a packet body.
            /// We use this as a convenient way to manage and cache a byte[] that's resized
            /// automatically to fit our payload.
            /// </summary>
            private readonly MemoryStream _readBufferMemoryStream;

            /// <summary>
            /// A reusable buffer for writing packets.
            /// </summary>
            private readonly MemoryStream _writeBufferMemoryStream;

            private readonly ITranslator _readTranslator;

            private readonly ITranslator _writeTranslator;

#if FEATURE_APM
            // cached delegates for pipe stream read callbacks
            private readonly AsyncCallback _headerReadCompleteCallback;
            private readonly AsyncCallback _bodyReadCompleteCallback;
#endif

            /// <summary>
            /// A queue used for enqueuing packets to write to the stream asynchronously.
            /// </summary>
            private readonly ConcurrentQueue<INodePacket> _packetWriteQueue;

            /// <summary>
            /// Delegate called when the context terminates.
            /// </summary>
            private readonly NodeContextTerminateDelegate _terminateDelegate;

            /// <summary>
            /// A task representing the work to consume enqueued packets.
            /// </summary>
            private readonly Task _packetWriteDrainTask;

            /// <summary>
            /// Used to signal the consuming task that a packet has been enqueued.
            /// </summary>
            private readonly SemaphoreSlim _packetEnqueued;

            /// <summary>
            /// Tracks the state of the packet sent to terminate the node.
            /// </summary>
            private ExitPacketState _exitPacketState;

#if FEATURE_APM
            // used in BodyReadComplete callback to avoid allocations due to passing state through BeginRead
            private int _currentPacketLength;
#endif

            /// <summary>
            /// Constructor.
            /// </summary>
            public NodeContext(int nodeId, Process process,
                Stream nodePipe,
                INodePacketFactory factory, NodeContextTerminateDelegate terminateDelegate)
            {
                _nodeId = nodeId;
                _process = process;
                _pipeStream = nodePipe;
                _packetFactory = factory;
                _headerByte = new byte[5]; // 1 for the packet type, 4 for the body length
                _readBufferMemoryStream = new MemoryStream();
                _writeBufferMemoryStream = new MemoryStream();
                _readTranslator = BinaryTranslator.GetReadTranslator(_readBufferMemoryStream, InterningBinaryReader.CreateSharedBuffer());
                _writeTranslator = BinaryTranslator.GetWriteTranslator(_writeBufferMemoryStream);
                _terminateDelegate = terminateDelegate;
#if FEATURE_APM
                _headerReadCompleteCallback = HeaderReadComplete;
                _bodyReadCompleteCallback = BodyReadComplete;
                _currentPacketLength = 0;
#endif

                _packetWriteQueue = new ConcurrentQueue<INodePacket>();
                _packetEnqueued = new SemaphoreSlim(0, 1);

                _packetWriteDrainTask = DrainPacketQueue();
            }

            /// <summary>
            /// Id of node.
            /// </summary>
            public int NodeId => _nodeId;

            /// <summary>
            /// Starts a new asynchronous read operation for this node.
            /// </summary>
            public void BeginAsyncPacketRead()
            {
#if FEATURE_APM
                _pipeStream.BeginRead(_headerByte, 0, _headerByte.Length, _headerReadCompleteCallback, this);
#else
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var ignored = RunPacketReadLoopAsync();
                });
#endif
            }

#if !FEATURE_APM
            public async Task RunPacketReadLoopAsync()
            {
                while (true)
                {
                    try
                    {
                        int bytesRead = await _pipeStream.ReadAsync(_headerByte.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                        if (!ProcessHeaderBytesRead(bytesRead))
                        {
                            return;
                        }
                    }
                    catch (IOException e)
                    {
                        CommunicationsUtilities.Trace(_nodeId, "EXCEPTION in RunPacketReadLoopAsync: {0}", e);
                        _packetFactory.RoutePacket(_nodeId, new NodeShutdown(NodeShutdownReason.ConnectionFailed));
                        Close();
                        return;
                    }

                    NodePacketType packetType = (NodePacketType)_headerByte[0];
                    int packetLength = BinaryPrimitives.ReadInt32LittleEndian(new Span<byte>(_headerByte, 1, 4));

                    _readBufferMemoryStream.SetLength(packetLength);
                    byte[] packetData = _readBufferMemoryStream.GetBuffer();

                    try
                    {
                        int totalBytesRead = 0;
                        while (totalBytesRead < packetLength)
                        {
                            int bytesRead = await _pipeStream.ReadAsync(packetData.AsMemory(totalBytesRead, packetLength - totalBytesRead), CancellationToken.None).ConfigureAwait(false);
                            if (bytesRead == 0)
                            {
                                break;
                            }

                            totalBytesRead += bytesRead;
                        }

                        if (!ProcessBodyBytesRead(totalBytesRead, packetLength, packetType))
                        {
                            return;
                        }
                    }
                    catch (IOException e)
                    {
                        CommunicationsUtilities.Trace(_nodeId, "EXCEPTION in RunPacketReadLoopAsync (Reading): {0}", e);
                        _packetFactory.RoutePacket(_nodeId, new NodeShutdown(NodeShutdownReason.ConnectionFailed));
                        Close();
                        return;
                    }

                    // Read and route the packet.
                    if (!ReadAndRoutePacket(packetType))
                    {
                        return;
                    }

                    if (packetType == NodePacketType.NodeShutdown)
                    {
                        Close();
                        return;
                    }
                }
            }
#endif

            /// <summary>
            /// Sends the specified packet to this node asynchronously.
            /// The method enqueues a task to write the packet and returns
            /// immediately. This is because SendData() is on a hot path
            /// under the primary lock (BuildManager's _syncLock)
            /// and we want to minimize our time there.
            /// </summary>
            /// <param name="packet">The packet to send.</param>
            public void SendData(INodePacket packet)
            {
                if (IsExitPacket(packet))
                {
                    _exitPacketState = ExitPacketState.ExitPacketQueued;
                }

                _packetWriteQueue.Enqueue(packet);

                if (_packetEnqueued.CurrentCount == 0)
                {
                    // If the semaphore is not already signaled, signal it to wake up the draining task.
                    _packetEnqueued.Release();
                }
            }

            /// <summary>
            /// Use a threadpool thread to drain the queue and be careful not to block it where possible.
            /// </summary>
            /// <remarks>Usually there'll be a single packet in the queue, but sometimes
            /// a burst of SendData comes in, with 10-20 packets scheduled.</remarks>
            private async Task DrainPacketQueue()
            {
                MemoryStream writeStream = _writeBufferMemoryStream;
                Stream serverToClientStream = _pipeStream;
                ITranslator writeTranslator = _writeTranslator;

                while (true)
                {
                    await _packetEnqueued.WaitAsync();
                    while (_packetWriteQueue.TryDequeue(out INodePacket packet))
                    {
                        // clear the buffer but keep the underlying capacity to avoid reallocations
                        writeStream.SetLength(0);

                        try
                        {
                            writeStream.WriteByte((byte)packet.Type);

                            // Pad for the packet length
                            WriteInt32(writeStream, 0);
                            packet.Translate(writeTranslator);

                            int writeStreamLength = (int)writeStream.Position;

                            // Now plug in the real packet length
                            writeStream.Position = 1;
                            WriteInt32(writeStream, writeStreamLength - 5);

                            byte[] writeStreamBuffer = writeStream.GetBuffer();

                            for (int i = 0; i < writeStreamLength; i += MaxPacketWriteSize)
                            {
                                int lengthToWrite = Math.Min(writeStreamLength - i, MaxPacketWriteSize);
#if NET
                                await serverToClientStream.WriteAsync(writeStreamBuffer.AsMemory(i, lengthToWrite));
#else
                                await serverToClientStream.WriteAsync(writeStreamBuffer, i, lengthToWrite);
#endif
                            }

                            if (IsExitPacket(packet))
                            {
                                _exitPacketState = ExitPacketState.ExitPacketSent;

                                return;
                            }

                            if (packet is NodeBuildComplete)
                            {
                                return;
                            }
                        }
                        catch (IOException e)
                        {
                            // Do nothing here because any exception will be caught by the async read handler
                            CommunicationsUtilities.Trace(_nodeId, "EXCEPTION in SendData: {0}", e);
                        }
                        catch (ObjectDisposedException) // This happens if a child dies unexpectedly
                        {
                            // Do nothing here because any exception will be caught by the async read handler
                        }
                    }
                }
            }

            private static bool IsExitPacket(INodePacket packet)
            {
                return packet is NodeBuildComplete buildCompletePacket && !buildCompletePacket.PrepareForReuse;
            }

            /// <summary>
            /// Avoid having a BinaryWriter just to write a 4-byte int
            /// </summary>
            private static void WriteInt32(MemoryStream stream, int value)
            {
                stream.WriteByte((byte)value);
                stream.WriteByte((byte)(value >> 8));
                stream.WriteByte((byte)(value >> 16));
                stream.WriteByte((byte)(value >> 24));
            }

            /// <summary>
            /// Closes the node's context, disconnecting it from the node.
            /// </summary>
            private void Close()
            {
                _pipeStream.Dispose();
                _packetEnqueued.Dispose();
                _terminateDelegate(_nodeId);
            }

            /// <summary>
            /// Waits for the child node process to exit.
            /// </summary>
            public async Task WaitForExitAsync(ILoggingService loggingService)
            {
                if (_exitPacketState == ExitPacketState.ExitPacketQueued)
                {
                    // Wait up to 100ms until all remaining packets are sent.
                    // We don't need to wait long, just long enough for the Task to start running on the ThreadPool.
#if NET
                    await _packetWriteDrainTask.WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
#else
                    using (var cts = new CancellationTokenSource(100))
                    {
                        await Task.WhenAny(_packetWriteDrainTask, Task.Delay(100, cts.Token));
                        cts.Cancel();
                    }
#endif
                }

                if (_exitPacketState == ExitPacketState.ExitPacketSent)
                {
                    CommunicationsUtilities.Trace("Waiting for node with pid = {0} to exit", _process.Id);

                    // .NET 5 introduces a real WaitForExitAsyc.
                    // This is a poor man's implementation that uses polling.
                    int timeout = TimeoutForWaitForExit;
                    int delay = 5;
                    while (timeout > 0)
                    {
                        bool exited = _process.WaitForExit(milliseconds: 0);
                        if (exited)
                        {
                            return;
                        }
                        timeout -= delay;
                        await Task.Delay(delay).ConfigureAwait(false);

                        // Double delay up to 500ms.
                        delay = Math.Min(delay * 2, 500);
                    }
                }

                // Kill the child and do a blocking wait.
                loggingService?.LogWarning(
                    BuildEventContext.Invalid,
                    null,
                    BuildEventFileInfo.Empty,
                    "KillingProcessWithPid",
                    _process.Id);
                CommunicationsUtilities.Trace("Killing node with pid = {0}", _process.Id);

                _process.KillTree(timeoutMilliseconds: 5000);
            }

            private bool ProcessHeaderBytesRead(int bytesRead)
            {
                if (bytesRead != _headerByte.Length)
                {
                    CommunicationsUtilities.Trace(_nodeId, "COMMUNICATIONS ERROR (HRC) Node: {0} Process: {1} Bytes Read: {2} Expected: {3}", _nodeId, _process.Id, bytesRead, _headerByte.Length);
                    try
                    {
                        if (_process.HasExited)
                        {
                            CommunicationsUtilities.Trace(_nodeId, "   Child Process {0} has exited.", _process.Id);
                        }
                        else
                        {
                            CommunicationsUtilities.Trace(_nodeId, "   Child Process {0} is still running.", _process.Id);
                        }
                    }
                    catch (Exception e) when (!ExceptionHandling.IsCriticalException(e))
                    {
                        CommunicationsUtilities.Trace(_nodeId, "Unable to retrieve remote process information. {0}", e);
                    }

                    _packetFactory.RoutePacket(_nodeId, new NodeShutdown(NodeShutdownReason.ConnectionFailed));
                    Close();
                    return false;
                }

                return true;
            }

#if FEATURE_APM
            /// <summary>
            /// Callback invoked by the completion of a read of a header byte on one of the named pipes.
            /// </summary>
            private void HeaderReadComplete(IAsyncResult result)
            {
                int bytesRead;
                try
                {
                    try
                    {
                        bytesRead = _pipeStream.EndRead(result);
                    }

                    // Workaround for CLR stress bug; it sporadically calls us twice on the same async
                    // result, and EndRead will throw on the second one. Pretend the second one never happened.
                    catch (ArgumentException)
                    {
                        CommunicationsUtilities.Trace(_nodeId, "Hit CLR bug #825607: called back twice on same async result; ignoring");
                        return;
                    }

                    if (!ProcessHeaderBytesRead(bytesRead))
                    {
                        return;
                    }
                }
                catch (IOException e)
                {
                    CommunicationsUtilities.Trace(_nodeId, "EXCEPTION in HeaderReadComplete: {0}", e);
                    _packetFactory.RoutePacket(_nodeId, new NodeShutdown(NodeShutdownReason.ConnectionFailed));
                    Close();
                    return;
                }

                _currentPacketLength = BinaryPrimitives.ReadInt32LittleEndian(new Span<byte>(_headerByte, 1, 4));
                MSBuildEventSource.Log.PacketReadSize(_currentPacketLength);

                // Ensures the buffer is at least this length.
                // It avoids reallocations if the buffer is already large enough.
                _readBufferMemoryStream.SetLength(_currentPacketLength);
                byte[] packetData = _readBufferMemoryStream.GetBuffer();

                _pipeStream.BeginRead(packetData, 0, _currentPacketLength, _bodyReadCompleteCallback, this);
            }
#endif

            private bool ProcessBodyBytesRead(int bytesRead, int packetLength, NodePacketType packetType)
            {
                if (bytesRead != packetLength)
                {
                    CommunicationsUtilities.Trace(_nodeId, "Bad packet read for packet {0} - Expected {1} bytes, got {2}", packetType, packetLength, bytesRead);
                    _packetFactory.RoutePacket(_nodeId, new NodeShutdown(NodeShutdownReason.ConnectionFailed));
                    Close();
                    return false;
                }
                return true;
            }

            private bool ReadAndRoutePacket(NodePacketType packetType)
            {
                try
                {
                    _readBufferMemoryStream.Position = 0;
                    _packetFactory.DeserializeAndRoutePacket(_nodeId, packetType, _readTranslator);
                }
                catch (IOException e)
                {
                    CommunicationsUtilities.Trace(_nodeId, "EXCEPTION in ReadAndRoutPacket: {0}", e);
                    _packetFactory.RoutePacket(_nodeId, new NodeShutdown(NodeShutdownReason.ConnectionFailed));
                    Close();
                    return false;
                }

                return true;
            }

#if FEATURE_APM
            /// <summary>
            /// Method called when the body of a packet has been read.
            /// </summary>
            private void BodyReadComplete(IAsyncResult result)
            {
                NodePacketType packetType = (NodePacketType)_headerByte[0];
                int bytesRead;

                try
                {
                    try
                    {
                        bytesRead = _pipeStream.EndRead(result);
                    }

                    // Workaround for CLR stress bug; it sporadically calls us twice on the same async
                    // result, and EndRead will throw on the second one. Pretend the second one never happened.
                    catch (ArgumentException)
                    {
                        CommunicationsUtilities.Trace(_nodeId, "Hit CLR bug #825607: called back twice on same async result; ignoring");
                        return;
                    }

                    if (!ProcessBodyBytesRead(bytesRead, _currentPacketLength, packetType))
                    {
                        return;
                    }
                }
                catch (IOException e)
                {
                    CommunicationsUtilities.Trace(_nodeId, "EXCEPTION in BodyReadComplete (Reading): {0}", e);
                    _packetFactory.RoutePacket(_nodeId, new NodeShutdown(NodeShutdownReason.ConnectionFailed));
                    Close();
                    return;
                }

                // Read and route the packet.
                if (!ReadAndRoutePacket(packetType))
                {
                    return;
                }

                if (packetType != NodePacketType.NodeShutdown)
                {
                    // Read the next packet.
                    BeginAsyncPacketRead();
                }
                else
                {
                    Close();
                }
            }
#endif
        }
    }
}
