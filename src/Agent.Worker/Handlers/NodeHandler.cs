﻿using Microsoft.VisualStudio.Services.Agent.Util;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Handlers
{
    [ServiceLocator(Default = typeof(NodeHandler))]
    public interface INodeHandler : IHandler
    {
        NodeHandlerData Data { get; set; }
    }

    public sealed class NodeHandler : Handler, INodeHandler
    {
        public NodeHandlerData Data { get; set; }

        public async Task RunAsync()
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Data, nameof(Data));
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(Inputs, nameof(Inputs));
            ArgUtil.Directory(TaskDirectory, nameof(TaskDirectory));

#if !OS_WINDOWS
            // Ensure compat vso-task-lib exist at the root of _work folder
            // This will make vsts-agent works against 2015 RTM/QU1 TFS, since tasks in those version doesn't package with task lib
            // Put the 0.5.5 version vso-task-lib into the root of _work/node_modules folder, so tasks are able to find those lib.
            if (!File.Exists(Path.Combine(IOUtil.GetWorkPath(HostContext), "node_modules", "vso-task-lib", "package.json")))
            {
                string vsoTaskLibFromExternal = Path.Combine(IOUtil.GetExternalsPath(), "vso-task-lib");
                string compatVsoTaskLibInWork = Path.Combine(IOUtil.GetWorkPath(HostContext), "node_modules", "vso-task-lib");
                IOUtil.CopyDirectory(vsoTaskLibFromExternal, compatVsoTaskLibInWork, ExecutionContext.CancellationToken);
            }
#endif

            // Update the env dictionary.
            AddInputsToEnvironment();
            AddEndpointsToEnvironment();
            AddSecureFilesToEnvironment();
            AddVariablesToEnvironment();
            AddTaskVariablesToEnvironment();
            AddPrependPathToEnvironment();

            // Resolve the target script.
            string target = Data.Target;
            ArgUtil.NotNullOrEmpty(target, nameof(target));
            target = Path.Combine(TaskDirectory, target);
            ArgUtil.File(target, nameof(target));

            // Resolve the working directory.
            string workingDirectory = Data.WorkingDirectory;
            if (string.IsNullOrEmpty(workingDirectory))
            {
                if (!string.IsNullOrEmpty(ExecutionContext.Variables.System_DefaultWorkingDirectory))
                {
                    workingDirectory = ExecutionContext.Variables.System_DefaultWorkingDirectory;
                }
                else
                {
                    workingDirectory = ExecutionContext.Variables.Agent_WorkFolder;
                }
            }

            ArgUtil.Directory(workingDirectory, nameof(workingDirectory));

            // Setup the process invoker.
            using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
            {
                processInvoker.OutputDataReceived += OnDataReceived;
                processInvoker.ErrorDataReceived += OnDataReceived;
                bool useNode5 = ExecutionContext.Variables.Agent_UseNode5 ?? false;
                string node = Path.Combine(
                    IOUtil.GetExternalsPath(),
                    useNode5 ? "node-5.10.1" : "node",
                    "bin",
                    $"node{IOUtil.ExeExtension}");

                var arguments = new List<string>();
                if (!useNode5)
                {
                    // Set --no-warnings to help preserve back compat. The handler previously used node version v5.10.1.
                    // When the handler was changed to use node v6.10.3, node can emit certain warnings over stderr.
                    // Passing the --no-warnings flag disables the warnings.
                    //
                    // Note, passing the flag --no-warnings will not disable the warnings for downstream node processes.
                    // If customers run into issues with this problem, then they can set the env var NODE_NO_WARNINGS=1.
                    // The agent should not set this env var, since it is unknown whether the task author desires warning
                    // suppression for downstream processes. For example, if the custom task uses the node installer task
                    // to run ad hoc customer scripts, then it should not set the env var.
                    arguments.Add("--no-warnings");
                }

                // Wrap the script file in double quotes and escape double quotes. Note, double-quote is a valid
                // file name character on Linux.
                arguments.Add(StringUtil.Format(@"""{0}""", target.Replace(@"""", @"\""")));

#if OS_WINDOWS
                // It appears that node.exe outputs UTF8 when not in TTY mode.
                Encoding outputEncoding = Encoding.UTF8;
#else
                // Let .NET choose the default.
                Encoding outputEncoding = null;
#endif

                // Execute the process. Exit code 0 should always be returned.
                // A non-zero exit code indicates infrastructural failure.
                // Task failure should be communicated over STDOUT using ## commands.
                await processInvoker.ExecuteAsync(
                    workingDirectory: workingDirectory,
                    fileName: node,
                    arguments: string.Join(" ", arguments),
                    environment: Environment,
                    requireExitCodeZero: true,
                    outputEncoding: outputEncoding,
                    cancellationToken: ExecutionContext.CancellationToken);
            }
        }

        private void OnDataReceived(object sender, ProcessDataReceivedEventArgs e)
        {
            // This does not need to be inside of a critical section.
            // The logging queues and command handlers are thread-safe.
            if (!CommandManager.TryProcessCommand(ExecutionContext, e.Data))
            {
                ExecutionContext.Output(e.Data);
            }
        }
    }
}
