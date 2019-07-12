﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script.Rpc
{
    public class RpcInitializationService : IHostedService, IManagedHostedService
    {
        private readonly IOptionsMonitor<ScriptApplicationHostOptions> _applicationHostOptions;
        private readonly IEnvironment _environment;
        private readonly IWebHostLanguageWorkerChannelManager _webHostlanguageWorkerChannelManager;
        private readonly IRpcServer _rpcServer;
        private readonly ILogger _logger;

        private readonly string _workerRuntime;
        private readonly int _rpcServerShutdownTimeoutInMilliseconds;

        private Dictionary<OSPlatform, List<string>> _hostingOSToWhitelistedRuntimes = new Dictionary<OSPlatform, List<string>>()
        {
            {
                OSPlatform.Windows,
                new List<string>() { LanguageWorkerConstants.JavaLanguageWorkerName }
            },
            {
                OSPlatform.Linux,
                new List<string>() { LanguageWorkerConstants.PythonLanguageWorkerName }
            }
        };

        // _webHostLevelWhitelistedRuntimes are started at webhost level when running in Azure and locally
        private List<string> _webHostLevelWhitelistedRuntimes = new List<string>()
        {
            LanguageWorkerConstants.JavaLanguageWorkerName
        };

        public RpcInitializationService(IOptionsMonitor<ScriptApplicationHostOptions> applicationHostOptions, IEnvironment environment, IRpcServer rpcServer, IWebHostLanguageWorkerChannelManager languageWorkerChannelManager, ILogger<RpcInitializationService> logger)
        {
            _applicationHostOptions = applicationHostOptions ?? throw new ArgumentNullException(nameof(applicationHostOptions));
            _logger = logger;
            _rpcServer = rpcServer;
            _environment = environment;
            _rpcServerShutdownTimeoutInMilliseconds = 5000;
            _webHostlanguageWorkerChannelManager = languageWorkerChannelManager ?? throw new ArgumentNullException(nameof(languageWorkerChannelManager));
            _workerRuntime = _environment.GetEnvironmentVariable(LanguageWorkerConstants.FunctionWorkerRuntimeSettingName);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Utility.CheckAppOffline(_applicationHostOptions.CurrentValue.ScriptPath))
            {
                return;
            }
            _logger.LogDebug("Starting Rpc Initialization Service.");
            await InitializeRpcServerAsync();
            await InitializeChannelsAsync();
            _logger.LogDebug("Rpc Initialization Service started.");
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Shuttingdown Rpc Channels Manager");
            _webHostlanguageWorkerChannelManager.ShutdownChannels();
            return Task.CompletedTask;
        }

        public async Task StopServicesAsync(CancellationToken cancellationToken)
        {
            await ShutdownRpcServerAsync();
        }

        internal async Task InitializeRpcServerAsync()
        {
            try
            {
                _logger.LogDebug("Initializing RpcServer");
                await _rpcServer.StartAsync();
                _logger.LogDebug("RpcServer initialized");
            }
            catch (Exception grpcInitEx)
            {
                var hostInitEx = new HostInitializationException($"Failed to start Rpc Server. Check if your app is hitting connection limits.", grpcInitEx);
            }
        }

        internal Task InitializeChannelsAsync()
        {
            if (ShouldStartInPlaceholderMode())
            {
                return InitializePlaceholderChannelsAsync();
            }

            return InitializeWebHostRuntimeChannelsAsync();
        }

        private async Task ShutdownRpcServerAsync()
        {
            _logger.LogDebug($"Shutting down RPC server");

            Task shutDownRpcServer = _rpcServer.ShutdownAsync();
            Task shutDownRpcServerWithContinuation = shutDownRpcServer
                                                        .ContinueWith(t => HandleFaultedShutdownOrKillOfRpcServer(t, "Shutting down"), TaskContinuationOptions.ExecuteSynchronously);

            Task shutDownWithTimeout = await Task.WhenAny(shutDownRpcServerWithContinuation, Task.Delay(_rpcServerShutdownTimeoutInMilliseconds));

            if (!shutDownWithTimeout.Equals(shutDownRpcServerWithContinuation) || shutDownRpcServer.IsFaulted)
            {
                _logger.LogDebug($"Killing RPC server");
                await _rpcServer.KillAsync()
                          .ContinueWith(t => HandleFaultedShutdownOrKillOfRpcServer(t, "Killing"), TaskContinuationOptions.ExecuteSynchronously);
            }
        }

        private void HandleFaultedShutdownOrKillOfRpcServer(Task t, string operation)
        {
            if (t.IsFaulted)
            {
                t.Exception.Handle(e =>
                {
                    _logger.LogError($"{operation} RPC server encountered EXCEPTION:'{e}' INNEREXCEPTION:'{e.InnerException}'");
                    return true;
                });
            }
        }

        private Task InitializePlaceholderChannelsAsync()
        {
            if (_environment.IsLinuxHostingEnvironment())
            {
                return InitializePlaceholderChannelsAsync(OSPlatform.Linux);
            }

            return InitializePlaceholderChannelsAsync(OSPlatform.Windows);
        }

        private Task InitializePlaceholderChannelsAsync(OSPlatform os)
        {
            return Task.WhenAll(_hostingOSToWhitelistedRuntimes[os].Select(runtime =>
                _webHostlanguageWorkerChannelManager.InitializeChannelAsync(runtime)));
        }

        private Task InitializeWebHostRuntimeChannelsAsync()
        {
            if (_webHostLevelWhitelistedRuntimes.Contains(_workerRuntime))
            {
                return _webHostlanguageWorkerChannelManager.InitializeChannelAsync(_workerRuntime);
            }

            return Task.CompletedTask;
        }

        private bool ShouldStartInPlaceholderMode()
        {
            return string.IsNullOrEmpty(_workerRuntime) && _environment.IsPlaceholderModeEnabled();
        }

        // To help with unit tests
        internal void AddSupportedWebHostLevelRuntime(string language) => _webHostLevelWhitelistedRuntimes.Add(language);
    }
}
