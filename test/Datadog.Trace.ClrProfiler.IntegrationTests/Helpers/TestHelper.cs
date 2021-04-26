using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Datadog.Core.Tools;
using Datadog.Trace.TestHelpers;
using Xunit;
using Xunit.Abstractions;

#if NETFRAMEWORK
using Datadog.Trace.ExtensionMethods; // needed for Dictionary<K,V>.GetValueOrDefault()
#endif

namespace Datadog.Trace.ClrProfiler.IntegrationTests
{
    public abstract class TestHelper
    {
        protected TestHelper(string sampleAppName, string samplePathOverrides, ITestOutputHelper output)
            : this(new EnvironmentHelper(sampleAppName, typeof(TestHelper), output, samplePathOverrides), output)
        {
        }

        protected TestHelper(string sampleAppName, ITestOutputHelper output)
            : this(new EnvironmentHelper(sampleAppName, typeof(TestHelper), output), output)
        {
        }

        protected TestHelper(EnvironmentHelper environmentHelper, ITestOutputHelper output)
        {
            EnvironmentHelper = environmentHelper;
            Output = output;

            Output.WriteLine($"Platform: {EnvironmentTools.GetPlatform()}");
            Output.WriteLine($"Configuration: {EnvironmentTools.GetBuildConfiguration()}");
            Output.WriteLine($"TargetFramework: {EnvironmentHelper.GetTargetFramework()}");
            Output.WriteLine($".NET Core: {EnvironmentHelper.IsCoreClr()}");
            Output.WriteLine($"Profiler DLL: {EnvironmentHelper.GetProfilerPath()}");
        }

        protected EnvironmentHelper EnvironmentHelper { get; }

        protected string TestPrefix => $"{EnvironmentTools.GetBuildConfiguration()}.{EnvironmentHelper.GetTargetFramework()}";

        protected ITestOutputHelper Output { get; }

        public Process StartSample(int traceAgentPort, string arguments, string packageVersion, int aspNetCorePort, int? statsdPort = null, string framework = "")
        {
            // get path to sample app that the profiler will attach to
            string sampleAppPath = EnvironmentHelper.GetSampleApplicationPath(packageVersion, framework);
            if (!File.Exists(sampleAppPath))
            {
                throw new Exception($"application not found: {sampleAppPath}");
            }

            // get full paths to integration definitions
            IEnumerable<string> integrationPaths = Directory.EnumerateFiles(".", "*integrations.json").Select(Path.GetFullPath);

            Output.WriteLine($"Starting Application: {sampleAppPath}");
            return ProfilerHelper.StartProcessWithProfiler(
                EnvironmentHelper.GetSampleExecutionSource(),
                sampleAppPath,
                EnvironmentHelper,
                integrationPaths,
                arguments,
                traceAgentPort: traceAgentPort,
                statsdPort: statsdPort,
                aspNetCorePort: aspNetCorePort);
        }

        public ProcessResult RunSampleAndWaitForExit(int traceAgentPort, int? statsdPort = null, string arguments = null, string packageVersion = "", string framework = "")
        {
            var process = StartSample(traceAgentPort, arguments, packageVersion, aspNetCorePort: 5000, statsdPort: statsdPort, framework: framework);

            using var helper = new ProcessHelper(process);

            process.WaitForExit();
            helper.Drain();
            var exitCode = process.ExitCode;

            var standardOutput = helper.StandardOutput;

            if (!string.IsNullOrWhiteSpace(standardOutput))
            {
                Output.WriteLine($"StandardOutput:{Environment.NewLine}{standardOutput}");
            }

            var standardError = helper.ErrorOutput;

            if (!string.IsNullOrWhiteSpace(standardError))
            {
                Output.WriteLine($"StandardError:{Environment.NewLine}{standardError}");
            }

            return new ProcessResult(process, standardOutput, standardError, exitCode);
        }

        public Process StartIISExpress(int traceAgentPort, int iisPort)
        {
            // get full paths to integration definitions
            IEnumerable<string> integrationPaths = Directory.EnumerateFiles(".", "*integrations.json").Select(Path.GetFullPath);

            var exe = EnvironmentHelper.GetSampleExecutionSource();
            var args = new string[]
                {
                    $"/clr:v4.0",
                    $"/path:{EnvironmentHelper.GetSampleProjectDirectory()}",
                    $"/systray:false",
                    $"/port:{iisPort}",
                    $"/trace:info",
                };

            Output.WriteLine($"[webserver] starting {exe} {string.Join(" ", args)}");

            var process = ProfilerHelper.StartProcessWithProfiler(
                EnvironmentHelper.GetSampleExecutionSource(),
                EnvironmentHelper.GetSampleExecutionSource(),
                EnvironmentHelper,
                integrationPaths,
                arguments: string.Join(" ", args),
                redirectStandardInput: true,
                traceAgentPort: traceAgentPort);

            var wh = new EventWaitHandle(false, EventResetMode.AutoReset);

            Task.Run(() =>
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    Output.WriteLine($"[webserver][stdout] {line}");

                    if (line.Contains("IIS Express is running"))
                    {
                        wh.Set();
                    }
                }
            });

            Task.Run(() =>
            {
                string line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    Output.WriteLine($"[webserver][stderr] {line}");
                }
            });

            wh.WaitOne(5000);

            // Wait for iis express to finish starting up
            var retries = 5;
            while (true)
            {
                var usedPorts = IPGlobalProperties.GetIPGlobalProperties()
                                                  .GetActiveTcpListeners()
                                                  .Select(ipEndPoint => ipEndPoint.Port);

                if (usedPorts.Contains(iisPort))
                {
                    break;
                }

                retries--;

                if (retries == 0)
                {
                    throw new Exception("Gave up waiting for IIS Express.");
                }

                Thread.Sleep(1500);
            }

            return process;
        }

        protected void ValidateSpans<T>(IEnumerable<MockTracerAgent.Span> spans, Func<MockTracerAgent.Span, T> mapper, IEnumerable<T> expected)
        {
            var spanLookup = new Dictionary<T, int>();
            foreach (var span in spans)
            {
                var key = mapper(span);
                if (spanLookup.ContainsKey(key))
                {
                    spanLookup[key]++;
                }
                else
                {
                    spanLookup[key] = 1;
                }
            }

            var missing = new List<T>();
            foreach (var e in expected)
            {
                var found = spanLookup.ContainsKey(e);
                if (found)
                {
                    if (--spanLookup[e] <= 0)
                    {
                        spanLookup.Remove(e);
                    }
                }
                else
                {
                    missing.Add(e);
                }
            }

            foreach (var e in missing)
            {
                Assert.True(false, $"no span found for `{e}`, remaining spans: `{string.Join(", ", spanLookup.Select(kvp => $"{kvp.Key}").ToArray())}`");
            }
        }

        protected void EnableDebugMode()
        {
            EnvironmentHelper.DebugModeEnabled = true;
        }

        protected void SetEnvironmentVariable(string key, string value)
        {
            EnvironmentHelper.CustomEnvironmentVariables.Add(key, value);
        }

        protected void SetServiceVersion(string serviceVersion)
        {
            SetEnvironmentVariable("DD_VERSION", serviceVersion);
        }

        protected void SetCallTargetSettings(bool enableCallTarget, bool enableMethodInlining)
        {
            SetEnvironmentVariable("DD_TRACE_CALLTARGET_ENABLED", enableCallTarget ? "true" : "false");
            SetEnvironmentVariable("DD_CLR_ENABLE_INLINING", enableMethodInlining ? "true" : "false");
        }

        protected async Task AssertWebServerSpan(
            string path,
            MockTracerAgent agent,
            int httpPort,
            HttpStatusCode expectedHttpStatusCode,
            bool isError,
            string expectedAspNetErrorType,
            string expectedAspNetErrorMessage,
            string expectedErrorType,
            string expectedErrorMessage,
            string expectedSpanType,
            string expectedOperationName,
            string expectedAspNetResourceName,
            string expectedResourceName,
            string expectedServiceVersion,
            SerializableDictionary expectedTags = null)
        {
            IImmutableList<MockTracerAgent.Span> spans;

            using (var httpClient = new HttpClient())
            {
                // disable tracing for this HttpClient request
                httpClient.DefaultRequestHeaders.Add(HttpHeaderNames.TracingEnabled, "false");
                var testStart = DateTime.UtcNow;
                var response = await httpClient.GetAsync($"http://localhost:{httpPort}" + path);
                var content = await response.Content.ReadAsStringAsync();
                Output.WriteLine($"[http] {response.StatusCode} {content}");
                Assert.Equal(expectedHttpStatusCode, response.StatusCode);

                agent.SpanFilters.Add(IsServerSpan);

                spans = agent.WaitForSpans(
                    count: 2,
                    minDateTime: testStart,
                    returnAllOperations: true);

                Assert.True(spans.Count == 2, $"expected two span, saw {spans.Count}");
            }

            MockTracerAgent.Span aspnetSpan = spans.Where(s => s.Name == "aspnet.request").FirstOrDefault();
            MockTracerAgent.Span innerSpan = spans.Where(s => s.Name == expectedOperationName).FirstOrDefault();

            Assert.NotNull(aspnetSpan);
            Assert.Equal(expectedAspNetResourceName, aspnetSpan.Resource);

            Assert.NotNull(innerSpan);
            Assert.Equal(expectedResourceName, innerSpan.Resource);

            foreach (MockTracerAgent.Span span in spans)
            {
                // base properties
                Assert.Equal(expectedSpanType, span.Type);

                // errors
                Assert.Equal(isError, span.Error == 1);
                if (span == aspnetSpan)
                {
                    Assert.Equal(expectedAspNetErrorType, span.Tags.GetValueOrDefault(Tags.ErrorType));
                    Assert.Equal(expectedAspNetErrorMessage, span.Tags.GetValueOrDefault(Tags.ErrorMsg));
                }
                else if (span == innerSpan)
                {
                    Assert.Equal(expectedErrorType, span.Tags.GetValueOrDefault(Tags.ErrorType));
                    Assert.Equal(expectedErrorMessage, span.Tags.GetValueOrDefault(Tags.ErrorMsg));
                }

                // other tags
                Assert.Equal(SpanKinds.Server, span.Tags.GetValueOrDefault(Tags.SpanKind));
                Assert.Equal(expectedServiceVersion, span.Tags.GetValueOrDefault(Tags.Version));
            }

            if (expectedTags?.Values is not null)
            {
                foreach (var expectedTag in expectedTags)
                {
                    Assert.Equal(expectedTag.Value, innerSpan.Tags.GetValueOrDefault(expectedTag.Key));
                }
            }
        }

        protected async Task AssertAspNetSpanOnly(
            string path,
            MockTracerAgent agent,
            int httpPort,
            HttpStatusCode expectedHttpStatusCode,
            bool isError,
            string expectedErrorType,
            string expectedErrorMessage,
            string expectedSpanType,
            string expectedResourceName,
            string expectedServiceVersion)
        {
            IImmutableList<MockTracerAgent.Span> spans;

            using (var httpClient = new HttpClient())
            {
                // disable tracing for this HttpClient request
                httpClient.DefaultRequestHeaders.Add(HttpHeaderNames.TracingEnabled, "false");
                var testStart = DateTime.UtcNow;
                var response = await httpClient.GetAsync($"http://localhost:{httpPort}" + path);
                var content = await response.Content.ReadAsStringAsync();
                Output.WriteLine($"[http] {response.StatusCode} {content}");
                Assert.Equal(expectedHttpStatusCode, response.StatusCode);

                spans = agent.WaitForSpans(
                    count: 1,
                    minDateTime: testStart,
                    operationName: "aspnet.request",
                    returnAllOperations: true);

                Assert.True(spans.Count == 1, $"expected two span, saw {spans.Count}");
            }

            MockTracerAgent.Span span = spans[0];

            // base properties
            Assert.Equal(expectedResourceName, span.Resource);
            Assert.Equal(expectedSpanType, span.Type);

            // errors
            Assert.Equal(isError, span.Error == 1);
            Assert.Equal(expectedErrorType, span.Tags.GetValueOrDefault(Tags.ErrorType));
            Assert.Equal(expectedErrorMessage, span.Tags.GetValueOrDefault(Tags.ErrorMsg));

            // other tags
            Assert.Equal(SpanKinds.Server, span.Tags.GetValueOrDefault(Tags.SpanKind));
            Assert.Equal(expectedServiceVersion, span.Tags.GetValueOrDefault(Tags.Version));
        }

        private bool IsServerSpan(MockTracerAgent.Span span) =>
            span.Tags.GetValueOrDefault(Tags.SpanKind) == SpanKinds.Server;

        internal class TupleList<T1, T2> : List<Tuple<T1, T2>>
        {
            public void Add(T1 item, T2 item2)
            {
                Add(new Tuple<T1, T2>(item, item2));
            }
        }
    }
}
