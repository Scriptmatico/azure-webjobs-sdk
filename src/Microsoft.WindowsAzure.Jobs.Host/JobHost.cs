﻿﻿using System;
using System.Collections.Generic;
﻿using System.Globalization;
﻿using System.IO;
using System.Reflection;
using System.Threading;

namespace Microsoft.WindowsAzure.Jobs
{
    /// <summary>
    /// Defines properties and methods to locate Job methods and listen to trigger events in order
    /// to execute Job methods.
    /// </summary>
    public class JobHost
    {
        // Where we log things to. 
        // Null if logging is not supported (this is required for pumping).        
        private readonly string _runtimeConnectionString;

        // The user account that we listen on.
        // This is the account that the bindings resolve against.
        private readonly string _dataConnectionString;

        private JobHostContext _hostContext;

        internal const string LoggingConnectionStringName = "AzureJobsRuntime";
        internal const string DataConnectionStringName = "AzureJobsData";

        /// <summary>
        /// Initializes a new instance of the JobHost class, using a Windows Azure Storage connection string located
        /// in the connectionStrings section of the configuration file.
        /// </summary>
        public JobHost()
            : this(DefaultHooks())
        {
        }

        internal JobHost(JobHostTestHooks hooks)
        {
            _dataConnectionString = hooks.ConnectionStringProvider.GetConnectionString(DataConnectionStringName);
            _runtimeConnectionString = hooks.ConnectionStringProvider.GetConnectionString(LoggingConnectionStringName);

            if (String.IsNullOrEmpty(_runtimeConnectionString))
            {
                var msg = FormatConnectionStringValidationError("runtime", LoggingConnectionStringName,
                    "Windows Azure Storage account connection string is missing or empty.");
                throw new InvalidOperationException(msg);
            }

            Initialize(hooks);
        }

        /// <summary>
        /// Initializes a new instance of the JobHost class, using a single Windows Azure Storage connection string for
        /// both reading and writing data as well as logging.
        /// </summary>
        public JobHost(string dataAndRuntimeConnectionString)
            : this(dataAndRuntimeConnectionString, dataAndRuntimeConnectionString)
        {
        }

        /// <summary>
        /// Initializes a new instance of the JobHost class, using one Windows Azure Storage connection string for
        /// reading and writing data and another connection string for logging.
        /// </summary>
        public JobHost(string dataConnectionString, string runtimeConnectionString)
            : this(dataConnectionString, runtimeConnectionString, DefaultHooks())
        {
        }

        internal JobHost(string dataConnectionString, string runtimeConnectionString, JobHostTestHooks hooks)
        {
            _dataConnectionString = dataConnectionString;
            _runtimeConnectionString = runtimeConnectionString;

            Initialize(hooks);
        }

        private void Initialize(JobHostTestHooks hooks)
        {
            ValidateConnectionStrings(hooks.StorageValidator);

            // This will do heavy operations like indexing. 
            _hostContext = GetHostContext(hooks.TypeLocator);

            WriteAntaresManifest();
        }

        private void ValidateConnectionStrings(IStorageValidator storageValidator)
        {
            string dataConnectionStringValidationError;
            if (!storageValidator.TryValidateConnectionString(_dataConnectionString, out dataConnectionStringValidationError))
            {
                var msg = FormatConnectionStringValidationError("data", DataConnectionStringName, dataConnectionStringValidationError);
                throw new InvalidOperationException(msg);
            }
            if (_runtimeConnectionString != null)
            {
                if (_runtimeConnectionString != _dataConnectionString)
                {
                    string runtimeConnectionStringValidationError;
                    if (!storageValidator.TryValidateConnectionString(_runtimeConnectionString, out runtimeConnectionStringValidationError))
                    {
                        var msg = FormatConnectionStringValidationError("runtime", LoggingConnectionStringName, runtimeConnectionStringValidationError);
                        throw new InvalidOperationException(msg);
                    }
                }
            }
        }

        private static string FormatConnectionStringValidationError(string connectionStringType, string connectionStringName, string validationErrorMessage)
        {
            return String.Format(CultureInfo.CurrentCulture,
                "Failed to validate Windows Azure Jobs {0} connection string: {2}" + Environment.NewLine +
                "The Windows Azure Jobs connection string is specified by setting a connection string named '{1}' in the connectionStrings section of the .config file, " +
                "or with an environment variable named '{1}', or by using the constructor for JobHost that accepts connection strings.",
                connectionStringType, connectionStringName, validationErrorMessage);
        }

        static JobHostTestHooks DefaultHooks()
        {
            return new JobHostTestHooks
            {
                StorageValidator = new DefaultStorageValidator(),
                TypeLocator = new DefaultTypeLocator(),
                ConnectionStringProvider = new DefaultConnectionStringProvider()
            };
        }

        /// <summary>
        /// Gets the storage account name from the connection string.
        /// </summary>
        public string UserAccountName
        {
            get { return Utility.GetAccountName(_dataConnectionString); }
        }

        // When running in Antares, write out a manifest file.
        private static void WriteAntaresManifest()
        {
            string filename = Environment.GetEnvironmentVariable("JOB_EXTRA_INFO_URL_PATH");
            if (filename != null)
            {
                const string manifestContents = "/azurejobs";

                File.WriteAllText(filename, manifestContents);
            }
        }

        private JobHostContext GetHostContext(ITypeLocator typesLocator)
        {
            var hostContext = new JobHostContext(_dataConnectionString, _runtimeConnectionString, typesLocator);
            return hostContext;
        }

        /// <summary>
        /// Runs the jobs on a background thread and return immediately.
        /// The trigger listeners and jobs will execute on the background thread.
        /// </summary>
        public void RunOnBackgroundThread()
        {
            RunOnBackgroundThread(CancellationToken.None);
        }

        /// <summary>
        /// Runs the jobs on a background thread and return immediately.
        /// The trigger listeners and jobs will execute on the background thread.
        /// The thread exits when the cancellation token is signalled.
        /// </summary>
        /// <param name="token">The token to monitor for cancellation requests.</param>
        public void RunOnBackgroundThread(CancellationToken token)
        {
            Thread thread = new Thread(_ => RunAndBlock(token));
            thread.Start();
        }

        /// <summary>
        /// Runs the jobs on the current thread.
        /// The trigger listeners and jobs will execute on the current thread.
        /// </summary>
        public void RunAndBlock()
        {
            RunAndBlock(CancellationToken.None);
        }

        /// <summary>
        /// Runs the jobs on the current thread.
        /// The trigger listeners and jobs will execute on the current thread.
        /// The thread will be blocked until the cancellation token is signalled.
        /// </summary>
        /// <param name="token">The token to monitor for cancellation requests.</param>
        public void RunAndBlock(CancellationToken token)
        {
            RunAndBlock(token, () =>
            {
                Thread.Sleep(2 * 1000);
                Console.Write(".");
            });
        }

        // Run the jobs on the current thread. 
        // Execute as much work as possible, and then invoke pauseAction() when there's a pause in the work. 
        internal void RunAndBlock(CancellationToken token, Action pauseAction)
        {
            using (IntervalSeparationTimer timer = CreateHeartbeatTimer(hostIsRunning: true))
            {
                timer.Start(executeFirst: true);

                try
                {
                    INotifyNewBlobListener fastpathNotify = new NotifyNewBlobViaInMemory();

                    using (Worker worker = new Worker(_hostContext._functionTableLookup, _hostContext._queueFunction, fastpathNotify))
                    {
                        while (!token.IsCancellationRequested)
                        {
                            bool handled;
                            do
                            {
                                worker.Poll(token);

                                if (token.IsCancellationRequested)
                                {
                                    return;
                                }

                                handled = HandleExecutionQueue();
                            }
                            while (handled);

                            pauseAction();
                        }
                    }
                }
                finally
                {
                    timer.Stop();
                }
            }
        }

        private bool HandleExecutionQueue()
        {
            if (_hostContext._executionQueue != null)
            {
                try
                {
                    bool handled = QueueClient.ApplyToQueue<FunctionInvokeRequest>(request => HandleFromExecutionQueue(request), _hostContext._executionQueue);
                    return handled;
                }
                catch
                {
                    // Poision message. 
                }
            }
            return false;
        }

        private void HandleFromExecutionQueue(FunctionInvokeRequest request)
        {
            // Function was already queued (from the dashboard). So now we just need to activate it.
            //_ctx._queueFunction.Queue(request);
            IActivateFunction activate = (IActivateFunction)_hostContext._queueFunction; // ### Make safe. 
            activate.ActivateFunction(request.Id);
        }

        /// <summary>
        /// Invoke a specific function specified by the method parameter.
        /// </summary>
        /// <param name="method">A MethodInfo representing the job method to execute.</param>
        public void Call(MethodInfo method)
        {
            Call(method, arguments: null);
        }

        /// <summary>
        /// Invoke a specific function specified by the method parameter.
        /// </summary>
        /// <param name="method">A MethodInfo representing the job method to execute.</param>
        /// <param name="arguments">An object with public properties representing argument names and values to bind to the parameter tokens in the job method's arguments.</param>
        public void Call(MethodInfo method, object arguments)
        {
            if (method == null)
            {
                throw new ArgumentNullException("method");
            }

            IDictionary<string, string> args2 = ObjectBinderHelpers.ConvertObjectToDict(arguments);

            FunctionDefinition func = ResolveFunctionDefinition(method, _hostContext._functionTableLookup);
            FunctionInvokeRequest instance = Worker.GetFunctionInvocation(func, args2, null);

            instance.TriggerReason = new InvokeTriggerReason
            {
                Message = String.Format("This was function was programmatically called via the host APIs.")
            };

            ExecutionInstanceLogEntity logItem;

            using (IntervalSeparationTimer timer = CreateHeartbeatTimer(hostIsRunning: false))
            {
                timer.Start(executeFirst: true);

                try
                {
                    logItem = _hostContext._queueFunction.Queue(instance);
                }
                finally
                {
                    timer.Stop();
                }
            }

            VerifySuccess(logItem);
        }

        private IntervalSeparationTimer CreateHeartbeatTimer(bool hostIsRunning)
        {
            ICanFailCommand heartbeat = CreateHeartbeat(hostIsRunning);
            return LinearSpeedupTimerCommand.CreateTimer(heartbeat, RunningHost.HeartbeatSignalInterval, TimeSpan.FromSeconds(10));
        }

        private ICanFailCommand CreateHeartbeat(bool hostIsRunning)
        {
            ICanFailCommand terminationCommand = CreateTerminateProcessUponRequestCommand();

            if (!hostIsRunning)
            {
                return terminationCommand;
            }
            else
            {
                ICanFailCommand runningHostHeartbeat = CreateRunningHostHeartbeat();
                return new CompositeCanFailCommand(terminationCommand, runningHostHeartbeat);
            }
        }

        private UpdateHostHeartbeatCommand CreateRunningHostHeartbeat()
        {
            return new UpdateHostHeartbeatCommand(_hostContext.RunningHostTableWriter, _hostContext.HostName);
        }

        private TerminateProcessUponRequestCommand CreateTerminateProcessUponRequestCommand()
        {
            return new TerminateProcessUponRequestCommand(_hostContext.TerminationSignalReader, _hostContext.HostInstanceId);
        }

        // Throw if the function failed. 
        private static void VerifySuccess(ExecutionInstanceLogEntity logItem)
        {
            if (logItem.GetStatus() == FunctionInstanceStatus.CompletedFailed)
            {
                throw new Exception("Function failed: " + logItem.ExceptionMessage);
            }
        }

        private FunctionDefinition ResolveFunctionDefinition(MethodInfo method, IFunctionTableLookup functionTableLookup)
        {
            foreach (FunctionDefinition func in functionTableLookup.ReadAll())
            {
                MethodInfoFunctionLocation loc = func.Location as MethodInfoFunctionLocation;
                if (loc != null)
                {
                    if (loc.MethodInfo.Equals(method))
                    {
                        return func;
                    }
                }
            }

            string msg = String.Format("'{0}' can't be invoked from Azure Jobs. Is it missing Azure Jobs bindings?", method);
            throw new InvalidOperationException(msg);
        }
    }
}