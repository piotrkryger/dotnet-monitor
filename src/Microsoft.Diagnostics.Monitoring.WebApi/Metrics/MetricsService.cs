﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.Monitoring.EventPipe;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Monitoring.WebApi
{
    /// <summary>
    /// Periodically gets metrics from the app, and persists these to a metrics store.
    /// </summary>
    internal sealed class MetricsService : BackgroundService
    {
        private MetricsPipeline? _counterPipeline;
        private readonly IDiagnosticServices _services;
        private readonly MetricsStoreService _store;
        private readonly IOptionsMonitor<MetricsOptions> _optionsMonitor;
        private readonly IOptionsMonitor<GlobalCounterOptions> _counterOptions;
        private readonly IOptionsMonitor<ProcessFilterOptions> _processFilterMonitor;

        public MetricsService(IServiceProvider serviceProvider,
            IOptionsMonitor<MetricsOptions> optionsMonitor,
            IOptionsMonitor<GlobalCounterOptions> counterOptions,
            IOptionsMonitor<ProcessFilterOptions> processFilterMonitor,
            MetricsStoreService metricsStore)
        {
            _store = metricsStore;
            _services = serviceProvider.GetRequiredService<IDiagnosticServices>();
            _optionsMonitor = optionsMonitor;
            _counterOptions = counterOptions;
            _processFilterMonitor = processFilterMonitor;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_optionsMonitor.CurrentValue.GetEnabled())
            {
                return;
            }

            if (!_optionsMonitor.CurrentValue.AllowMultipleProcessesMetrics)
            {
                await GetMetricsFromSingleProcessAsync(stoppingToken);
            }
            else
            {
                await GetMetricsFromMultipleProcessesAsync(stoppingToken);
            }
        }

        private async Task GetMetricsFromSingleProcessAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                stoppingToken.ThrowIfCancellationRequested();

                try
                {
                    IProcessInfo process = await _services.GetProcessAsync(processKey: null, stoppingToken);
                    MetricsStore metricsStore = _store.MetricsStore;
                    await StartMetricsPipelineForProcessAsync(process, metricsStore, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    //Most likely we failed to resolve the pid or metric configuration change. Attempt to do this again.
                    if (_counterPipeline != null)
                    {
                        await _counterPipeline.DisposeAsync();
                    }
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        private async Task StartMetricsPipelineForProcessAsync(IProcessInfo process, MetricsStore metricsStore, CancellationToken stoppingToken)
        {
            var client = new DiagnosticsClient(process.EndpointInfo.Endpoint);

            MetricsOptions options = _optionsMonitor.CurrentValue;
            GlobalCounterOptions counterOptions = _counterOptions.CurrentValue;
            using var optionsTokenSource = new CancellationTokenSource();

            //If metric options change, we need to cancel the existing metrics pipeline and restart with the new settings.
            using IDisposable? monitorListener = _optionsMonitor.OnChange((_, _) => optionsTokenSource.SafeCancel());

            MetricsPipelineSettings counterSettings = MetricsSettingsFactory.CreateSettings(counterOptions, Timeout.Infinite, options);
            counterSettings.UseSharedSession = process.EndpointInfo.RuntimeVersion?.Major >= 8;

            _counterPipeline = new MetricsPipeline(client, counterSettings, loggers: new[] { new MetricsLogger(metricsStore) });

            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, optionsTokenSource.Token);
            await _counterPipeline.RunAsync(linkedTokenSource.Token);
        }

        private async Task GetMetricsFromMultipleProcessesAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                stoppingToken.ThrowIfCancellationRequested();

                try
                {
                    DiagProcessFilter filter = DiagProcessFilter.FromConfiguration(_processFilterMonitor.CurrentValue);
                    IEnumerable<IProcessInfo> processes = await _services
                        .GetProcessesAsync(filter, stoppingToken, matchAllFilters: false);

                    foreach (var process in processes)
                    {
                        int processId = process.EndpointInfo.ProcessId;
                        bool metricsAlreadyRegistered = _store.GetOrCreateStoreFor(process, out MetricsStore? metricsStore);
                        if (!metricsAlreadyRegistered)
                        {
                            var processMetrics = new SingleProcessMetricsService(_optionsMonitor,
                                _counterOptions.CurrentValue, process, metricsStore!);
                            _ = processMetrics.StartMetricsPipelineForProcessAsync(stoppingToken)
                                .ContinueWith(MetricsPipelineCleanUp, new MetricsPipelineState(processId, _store), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current);
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    //Most likely we failed to resolve the pid or metric configuration change. Attempt to do this again.
                }


                await Task.Delay(5000, stoppingToken);
            }
        }

        private record MetricsPipelineState(int ProcessId, MetricsStoreService MetricsService) { }
        private static void MetricsPipelineCleanUp(Task pipelineTask, object? stateObj)
        {
            var state = (MetricsPipelineState)stateObj!;
            state.MetricsService.RemoveMetricsForPid(state.ProcessId);
        }
        public override async void Dispose()
        {
            base.Dispose();
            if (_counterPipeline != null)
            {
                await _counterPipeline.DisposeAsync();
            }
        }
    }
}
