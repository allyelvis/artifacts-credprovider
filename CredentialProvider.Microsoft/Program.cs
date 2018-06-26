﻿// Copyright (c) Microsoft. All rights reserved.
//
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Protocol.Plugins;
using NuGetCredentialProvider.CredentialProviders;
using NuGetCredentialProvider.CredentialProviders.Vsts;
using NuGetCredentialProvider.CredentialProviders.VstsBuildTask;
using NuGetCredentialProvider.Logging;
using NuGetCredentialProvider.RequestHandlers;
using NuGetCredentialProvider.Util;
using PowerArgs;
using ILogger = NuGetCredentialProvider.Logging.ILogger;

namespace NuGetCredentialProvider
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            CancellationTokenSource tokenSource = new CancellationTokenSource();
            var parsedArgs = await Args.ParseAsync<CredentialProviderArgs>(args);
            var multiLogger = new MultiLogger();

            var fileLogger = GetFileLogger();
            if (fileLogger != null)
            {
                multiLogger.Add(fileLogger);
            }

            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                tokenSource.Cancel();
                eventArgs.Cancel = true;
            };

            List<ICredentialProvider> credentialProviders = new List<ICredentialProvider>
            {
                new VstsBuildTaskCredentialProvider(multiLogger),
                new VstsCredentialProvider(multiLogger),
            };

            try
            {
                IRequestHandlers requestHandlers = new RequestHandlerCollection
                {
                    { MessageMethod.GetAuthenticationCredentials, new GetAuthenticationCredentialsRequestHandler(multiLogger, credentialProviders) },
                    { MessageMethod.GetOperationClaims, new GetOperationClaimsRequestHandler(multiLogger, credentialProviders) },
                    { MessageMethod.Initialize, new InitializeRequestHandler(multiLogger) },
                    { MessageMethod.SetLogLevel, new SetLogLevelRequestHandler(multiLogger) },
                    { MessageMethod.SetCredentials, new SetCredentialsRequestHandler(multiLogger) },
                };

                // TODO: log version
                multiLogger.Verbose(string.Format(Resources.CommandLineArgs, Environment.CommandLine));

                // Plug-in mode
                if (parsedArgs.Plugin)
                {
                    multiLogger.Verbose(Resources.RunningInPlugin);

                    using (IPlugin plugin = await PluginFactory.CreateFromCurrentProcessAsync(requestHandlers, ConnectionOptions.CreateDefault(), tokenSource.Token).ConfigureAwait(continueOnCapturedContext: false))
                    {
                        multiLogger.Add(new PluginConnectionLogger(plugin.Connection));
                        await RunNuGetPluginsAsync(plugin, multiLogger, TimeSpan.FromMinutes(2), tokenSource.Token).ConfigureAwait(continueOnCapturedContext: false);
                    }

                    return 0;
                }

                // Stand-alone mode
                if (requestHandlers.TryGet(MessageMethod.GetAuthenticationCredentials, out IRequestHandler requestHandler) && requestHandler is GetAuthenticationCredentialsRequestHandler getAuthenticationCredentialsRequestHandler)
                {
                    multiLogger.Add(new ConsoleLogger());
                    multiLogger.SetLogLevel(parsedArgs.Verbosity);
                    multiLogger.Verbose(Resources.RunningInStandAlone);

                    if (parsedArgs.Uri == null)
                    {
                        Console.WriteLine(ArgUsage.GenerateUsageFromTemplate<CredentialProviderArgs>());
                        return 1;
                    }

                    GetAuthenticationCredentialsRequest request = new GetAuthenticationCredentialsRequest(parsedArgs.Uri, isRetry: parsedArgs.IsRetry, isNonInteractive: parsedArgs.NonInteractive);
                    GetAuthenticationCredentialsResponse response = await getAuthenticationCredentialsRequestHandler.HandleRequestAsync(request).ConfigureAwait(continueOnCapturedContext: false);

                    multiLogger.Info($"{Resources.Username}: {response?.Username}");
                    multiLogger.Info($"{Resources.Password}: {(parsedArgs.RedactPassword ? Resources.Redacted : response?.Password)}");
                    return 0;
                }

                return -1;
            }
            finally
            {
                foreach (ICredentialProvider credentialProvider in credentialProviders)
                {
                    credentialProvider.Dispose();
                }
            }
        }

        internal static async Task RunNuGetPluginsAsync(IPlugin plugin, ILogger logger, TimeSpan timeout, CancellationToken cancellationToken)
        {
            SemaphoreSlim semaphore = new SemaphoreSlim(0);

            plugin.Connection.Faulted += (sender, a) =>
            {
                logger.Error(string.Format(Resources.FaultedOnMessage, $"{a.Message?.Type} {a.Message?.Method} {a.Message?.RequestId}"));
                logger.Error(a.Exception.ToString());
            };

            plugin.Closed += (sender, a) => semaphore.Release();

            bool complete = await semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (!complete)
            {
                logger.Error(Resources.PluginTimedOut);
            }
        }

        private static FileLogger GetFileLogger()
        {
            var location = EnvUtil.FileLogLocation;
            if (string.IsNullOrEmpty(location))
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(location));
            return new FileLogger(location);
        }
    }
}