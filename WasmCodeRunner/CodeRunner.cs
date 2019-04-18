﻿using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace MLS.WasmCodeRunner
{

    public class CodeRunner
    {
        public static InteropMessage<WasmCodeRunnerResponse> ProcessRunRequest(string message)
        {
            var messageObject = JsonConvert.DeserializeObject<InteropMessage<WasmCodeRunnerRequest>>(message);
            if (messageObject.Data.Base64Assembly == null && messageObject.Data.Diagnostics == null)
            {
                // Something was posted that wasn't meant for us
                return null;
            }

            var sequence = messageObject.Sequence;
            var runRequest = messageObject.Data;

            if (runRequest.Succeeded)
            {
                return ExecuteRunRequest(runRequest, sequence);
            }

            var diagnostics = runRequest.Diagnostics.Select(d => d.Message);
            return new InteropMessage<WasmCodeRunnerResponse>(sequence, new WasmCodeRunnerResponse(false, null, diagnostics.ToArray(), null, null));
        }


        public static InteropMessage<WasmCodeRunnerResponse> ExecuteRunRequest(WasmCodeRunnerRequest runRequest, int sequence)
        {
            var output = new List<string>();
            string runnerException = null;
            var bytes = Convert.FromBase64String(runRequest.Base64Assembly);
            
            var stdOut = new StringWriter();
            var stdError = new StringWriter();
            var originalStdOut = Console.Out;
            var originalStdError = Console.Error;
            try
            {
                var assembly = Assembly.Load(bytes);

                Console.SetOut(stdOut);
                Console.SetError(stdError);

                var main = EntryPointDiscoverer.FindStaticEntryMethod(assembly);

                var args = runRequest.RunArgs;

                var builder = new CommandLineBuilder()
                    .ConfigureRootCommandFromMethod(main)
                    .UseExceptionHandler();

                var parser = builder.Build();
                parser.InvokeAsync(args).GetAwaiter().GetResult();

            }
            catch (InvalidProgramException)
            {
                var result = new WasmCodeRunnerResponse(succeeded: false, exception: null,
                    output: new[] { "error CS5001: Program does not contain a static 'Main' method suitable for an entry point" },
                    diagnostics: Array.Empty<SerializableDiagnostic>(),
                    runnerException: null);

                return new InteropMessage<WasmCodeRunnerResponse>(sequence, result);
            }
            catch (Exception e)
            {
                if ((e.InnerException ?? e) is TypeLoadException t)
                {
                    runnerException = $"Missing type `{t.TypeName}`";
                }
                if ((e.InnerException ?? e) is MissingMethodException m)
                {
                    runnerException = $"Missing method `{m.Message}`";
                }
                if ((e.InnerException ?? e) is FileNotFoundException f)
                {
                    runnerException = $"Missing file: `{f.FileName}`";
                }

                output.AddRange(SplitOnNewlines(e.ToString()));
            }

            var errors = stdError.ToString();
            if (!string.IsNullOrWhiteSpace(errors))
            {
                runnerException = errors;
                output.AddRange(SplitOnNewlines(errors));
            }

            output.AddRange(SplitOnNewlines(stdOut.ToString()));

            var rb = new WasmCodeRunnerResponse(
                succeeded: true,
                exception: null,
                output: output.ToArray(),
                diagnostics: null,
                runnerException: runnerException);

            return new InteropMessage<WasmCodeRunnerResponse>(sequence, rb);
        }

        private static IEnumerable<string> SplitOnNewlines(string str)
        {
            str = str.Replace("\r\n", "\n");
            return str.Split('\n');
        }
    }
}
