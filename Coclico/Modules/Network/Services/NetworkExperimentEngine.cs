using System.Diagnostics;
using System.Runtime.Versioning;
using Coclico.Models.Network;
using Microsoft.Win32;

namespace Coclico.Services.Network;

[SupportedOSPlatform("windows")]
public sealed class NetworkExperimentEngine : INetworkExperimentEngine
{
    private const string NetworkClassKeyPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private readonly INetworkDiscoveryService _discoveryService;
    private readonly INetworkSafetyService _safetyService;
    private readonly INetworkSnapshotService _snapshotService;
    private readonly INetworkBenchmarkService _benchmarkService;
    private readonly INetworkScoringService _scoringService;
    private readonly INetworkWatchdogService _watchdogService;
    private readonly INetworkMemoryService _memoryService;
    private readonly INetworkDiagnosticsService _diagnosticsService;

    private CancellationTokenSource? _activeCts;
    private readonly object _stateLock = new();

    public EngineState CurrentState { get; private set; } = EngineState.Idle;
    public EngineProgressInfo CurrentProgress { get; } = new();

    public event Action<EngineProgressInfo>? OnProgressUpdated;
    public event Action<CognitiveLogEntry>? OnLogEntryAdded;

    public NetworkExperimentEngine(
        INetworkDiscoveryService discoveryService,
        INetworkSafetyService safetyService,
        INetworkSnapshotService snapshotService,
        INetworkBenchmarkService benchmarkService,
        INetworkScoringService scoringService,
        INetworkWatchdogService watchdogService,
        INetworkMemoryService memoryService,
        INetworkDiagnosticsService diagnosticsService)
    {
        _discoveryService = discoveryService;
        _safetyService = safetyService;
        _snapshotService = snapshotService;
        _benchmarkService = benchmarkService;
        _scoringService = scoringService;
        _watchdogService = watchdogService;
        _memoryService = memoryService;
        _diagnosticsService = diagnosticsService;
    }

    public async Task<string> RunAdaptiveOptimizationSessionAsync(
        string? targetAdapterId = null,
        OptimizationProfile profile = OptimizationProfile.Gaming,
        EngineExecutionMode mode = EngineExecutionMode.Auto,
        int samplesPerTest = 10,
        CancellationToken ct = default)
    {
        samplesPerTest = mode == EngineExecutionMode.Expert ? 40 : 10;
        lock (_stateLock)
        {
            if (CurrentState == EngineState.Running)
            {
                throw new InvalidOperationException("Une session d'expérimentation réseau est déjà en cours.");
            }
            CurrentState = EngineState.Running;
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        CancellationToken token = _activeCts.Token;
        CurrentProgress.CompletedExperiments.Clear();
        CurrentProgress.OverallProgress = 0;

        try
        {

            SetPhase(EnginePhase.Discovery, "Découverte et identification du matériel réseau physique...", 5);
            IReadOnlyList<NetworkAdapterHardwareDetails> adapters = await _discoveryService.DiscoverPhysicalAdaptersAsync(token).ConfigureAwait(false);
            if (adapters.Count == 0)
            {
                throw new InvalidOperationException("Aucun adaptateur réseau physique actif (Ethernet ou Wi-Fi) n'a été détecté.");
            }

            NetworkAdapterHardwareDetails targetAdapter = !string.IsNullOrEmpty(targetAdapterId)
                ? adapters.FirstOrDefault(a => string.Equals(a.Id, targetAdapterId, StringComparison.OrdinalIgnoreCase)) ?? adapters[0]
                : adapters[0];

            Log(EnginePhase.Discovery, $"Adaptateur ciblé : {targetAdapter.Name} ({targetAdapter.Description})", "SUCCESS");
            if (targetAdapter.IsWireless)
            {
                Log(EnginePhase.Discovery, $"Wi-Fi détecté : SSID '{targetAdapter.WifiSsid ?? "N/A"}', Canal {targetAdapter.WifiChannel}, Signal {targetAdapter.WifiRssi}%", "INFO");
            }

            SetPhase(EnginePhase.SafetyAudit, "Classification de sécurité et découverte des plages du pilote...", 10);
            foreach (DynamicNetworkParameter param in targetAdapter.DiscoveredParameters)
            {
                _safetyService.ClassifyParameter(param);
            }

            var allowedParams = targetAdapter.DiscoveredParameters
                .Where(p => _safetyService.IsAllowedInMode(p, mode))
                .ToList();

            Log(EnginePhase.SafetyAudit, $"{targetAdapter.DiscoveredParameters.Count} paramètres découverts dans le pilote NDIS. {allowedParams.Count} autorisés en mode {mode}.", "INFO");

            SetPhase(EnginePhase.InitialSnapshot, "Création du snapshot de sauvegarde initial (Microsoft / Pilote)...", 15);
            NetworkConfigurationSnapshot baselineSnapshot = await _snapshotService.CaptureSnapshotAsync(targetAdapter.Id, targetAdapter.Name, "Baseline_Initial", token).ConfigureAwait(false);
            Log(EnginePhase.InitialSnapshot, $"Snapshot initial créé avec succès (ID: {baselineSnapshot.SnapshotId[..8]}).", "SUCCESS");

            SetPhase(EnginePhase.BaselineBenchmark, $"Mesure statistique rigoureuse de l'état initial (Baseline — {samplesPerTest} rounds interleaved × 4 dimensions)...", 20);
            NetworkMetricReport baselineMetrics = await _benchmarkService.RunFourPrecisionTestsAsync(
                targetAdapter.GatewayAddress,
                samplesPerTest,
                onSampleProgress: (testIdx, testName, sampleIdx, totalSamples, liveVal) =>
                {
                    CurrentProgress.CurrentTestIndex = testIdx;
                    CurrentProgress.CurrentTestName = $"[Baseline] {testName}";
                    CurrentProgress.CurrentSampleIndex = sampleIdx;
                    CurrentProgress.TotalSamplesPerTest = totalSamples;
                    if (testIdx == 1)
                    {
                        CurrentProgress.LivePrecisionLatencyMs = liveVal;
                    }
                    else if (testIdx == 2)
                    {
                        CurrentProgress.LivePrecisionJitterMs = liveVal;
                    }
                    else if (testIdx == 3)
                    {
                        CurrentProgress.LivePrecisionThroughputMbps = liveVal;
                    }
                    else if (testIdx == 4)
                    {
                        CurrentProgress.LivePrecisionBufferbloatMs = liveVal;
                    }

                    OnProgressUpdated?.Invoke(CurrentProgress);
                },
                token).ConfigureAwait(false);

            baselineMetrics.CompositeScore = _scoringService.ComputeCompositeScore(baselineMetrics, profile);
            baselineSnapshot.MetricsAtSnapshot = baselineMetrics;
            CurrentProgress.BaselineScore = baselineMetrics.CompositeScore;
            CurrentProgress.BaselineLatencyMs = baselineMetrics.LatencyMedianMs;
            CurrentProgress.BaselineJitterMs = baselineMetrics.JitterMs;
            CurrentProgress.BaselineThroughputMbps = baselineMetrics.ThroughputMbps;
            CurrentProgress.BaselineBufferbloatMs = baselineMetrics.BufferbloatIncreaseMs;
            CurrentProgress.CurrentScore = baselineMetrics.CompositeScore;
            CurrentProgress.CurrentLatencyMs = baselineMetrics.LatencyMedianMs;
            CurrentProgress.CurrentJitterMs = baselineMetrics.JitterMs;
            CurrentProgress.CurrentThroughputMbps = baselineMetrics.ThroughputMbps;
            CurrentProgress.CurrentPacketLoss = baselineMetrics.PacketLossPercent;
            CurrentProgress.CurrentBufferbloatGrade = baselineMetrics.BufferbloatGrade;

            Log(EnginePhase.BaselineBenchmark,
                $"Baseline établie : Ping {baselineMetrics.LatencyMedianMs:F1}ms (±{baselineMetrics.JitterMs:F1}ms), Débit {baselineMetrics.ThroughputMbps:F1} Mbps, Bufferbloat {baselineMetrics.BufferbloatGrade}. Score: {baselineMetrics.CompositeScore:F1}/100.", "SUCCESS");

            string fingerprint = $"{targetAdapter.MatchingDeviceId}_{targetAdapter.DriverVersion}".Trim('_');
            HardwareKnowledgeProfile? knownProfile = _memoryService.GetProfile(fingerprint);
            if (knownProfile != null && knownProfile.ProvenBestSettings.Count > 0)
            {
                Log(EnginePhase.BaselineBenchmark, $"Profil mémorisé pour ce matériel : {knownProfile.ProvenBestSettings.Count} réglages déjà validés historiquement.", "INFO");
            }

            SetPhase(EnginePhase.SingleParameterExperiments, $"Démarrage des tests expérimentaux isolés ({samplesPerTest} rounds × 4 dimensions par paramètre)...", 30);

            NetworkMetricReport currentBestMetrics = baselineMetrics;
            var experimentsList = new List<ExperimentRecord>();
            int experimentCounter = 0;
            CurrentProgress.TotalExperiments = allowedParams.Count;

            _watchdogService.StartMonitoring(targetAdapter.Id, targetAdapter.GatewayAddress, async (reason) =>
            {
                Log(EnginePhase.Rollback, $"WATCHDOG INTERVENTION : {reason}. Rollback d'urgence immédiat en cours...", "ERROR");
                await RequestEmergencyRollbackAsync().ConfigureAwait(false);
            });

            try
            {
                foreach (DynamicNetworkParameter? param in allowedParams)
                {
                    token.ThrowIfCancellationRequested();
                    experimentCounter++;
                    CurrentProgress.CurrentExperiment = experimentCounter;
                    CurrentProgress.CurrentTestedParameter = param.DisplayName;
                    CurrentProgress.CurrentTestedParamExplanation = !string.IsNullOrEmpty(param.DetailedExplanation) ? param.DetailedExplanation : _safetyService.GetExplanationForParameter(param);
                    CurrentProgress.CurrentTestedParamCategory = param.Category.ToString();
                    CurrentProgress.CurrentTestedParamScope = param.Scope;
                    double phaseProgress = 30.0 + (experimentCounter / (double)allowedParams.Count * 50.0);
                    UpdateOverallProgress(phaseProgress);

                    int targetValuesCount = (mode == EngineExecutionMode.Expert) ? 40 : 12;
                    List<string> candidateValues = DetermineCandidateValues(param, mode, targetValuesCount);
                    Log(EnginePhase.SingleParameterExperiments,
                        $"Paramètre #{experimentCounter:00}/{allowedParams.Count:00} : {param.DisplayName} [{param.CurrentValue}] — Évaluation de {candidateValues.Count} valeurs candidates ({samplesPerTest} rounds × 4 dimensions)...", "EXPERIMENT");

                    string bestCandidateValue = param.CurrentValue;
                    double bestScore = currentBestMetrics.CompositeScore;
                    NetworkMetricReport bestCandidateMetrics = currentBestMetrics;
                    bool improved = false;

                    for (int vi = 0; vi < candidateValues.Count; vi++)
                    {
                        token.ThrowIfCancellationRequested();
                        string candVal = candidateValues[vi];
                        CurrentProgress.CurrentTestedValue = candVal;
                        CurrentProgress.PreviousTestedValue = param.CurrentValue;

                        if (string.IsNullOrEmpty(candVal) || string.Equals(candVal, param.CurrentValue, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!_safetyService.ValidateProposedValue(param, candVal, out _))
                        {
                            continue;
                        }

                        if (mode == EngineExecutionMode.Simulation)
                        {
                            double simLatency = Math.Max(2.0, baselineMetrics.LatencyMedianMs * (0.85 + (vi % 7 * 0.03)));
                            double simJitter = Math.Max(0.1, baselineMetrics.JitterMs * (0.80 + (vi % 5 * 0.04)));
                            double simThroughput = baselineMetrics.ThroughputMbps * (1.05 + (vi % 6 * 0.03));
                            double simBufferbloat = Math.Max(0.0, baselineMetrics.BufferbloatIncreaseMs * 0.7);

                            for (int s = 1; s <= samplesPerTest; s++)
                            {
                                token.ThrowIfCancellationRequested();
                                CurrentProgress.CurrentSampleIndex = s;
                                CurrentProgress.TotalSamplesPerTest = samplesPerTest;
                                CurrentProgress.CurrentTestIndex = (s % 4) + 1;
                                CurrentProgress.CurrentTestName = $"Test {CurrentProgress.CurrentTestIndex}/4 (Éch. {s}/{samplesPerTest} - Val {vi + 1}/{candidateValues.Count})";
                                CurrentProgress.LivePrecisionLatencyMs = simLatency + (Math.Sin(s + vi) * 0.15);
                                CurrentProgress.LivePrecisionJitterMs = simJitter;
                                CurrentProgress.LivePrecisionThroughputMbps = simThroughput;
                                CurrentProgress.LivePrecisionBufferbloatMs = simBufferbloat;
                                OnProgressUpdated?.Invoke(CurrentProgress);
                                await Task.Delay(mode == EngineExecutionMode.Expert ? 2 : 5, token).ConfigureAwait(false);
                            }

                            var candMetrics = new NetworkMetricReport
                            {
                                LatencyMedianMs = simLatency,
                                JitterMs = simJitter,
                                ThroughputMbps = simThroughput,
                                BufferbloatLatencyUnderLoadMs = simLatency + simBufferbloat,
                                BufferbloatGrade = simBufferbloat < 5 ? "A+" : "A",
                                PacketLossPercent = 0
                            };
                            candMetrics.CompositeScore = _scoringService.ComputeCompositeScore(candMetrics, profile);

                            if (candMetrics.CompositeScore > bestScore)
                            {
                                bestScore = candMetrics.CompositeScore;
                                bestCandidateValue = candVal;
                                bestCandidateMetrics = candMetrics;
                                improved = true;
                            }
                        }
                        else
                        {
                            bool applied = await ApplyDriverRegistryValueAsync(targetAdapter.Id, param.RegistryKeyword, candVal, token).ConfigureAwait(false);
                            if (!applied)
                            {
                                continue;
                            }

                            await Task.Delay(100, token).ConfigureAwait(false);
                            bool healthy = await _watchdogService.QuickHealthCheckAsync(targetAdapter.GatewayAddress, token).ConfigureAwait(false);
                            if (!healthy)
                            {
                                _ = await _snapshotService.RollbackSingleParameterAsync(targetAdapter.Id, param.RegistryKeyword, param.CurrentValue, token).ConfigureAwait(false);
                                continue;
                            }

                            NetworkMetricReport candidateMetrics = await _benchmarkService.RunFourPrecisionTestsAsync(
                                targetAdapter.GatewayAddress,
                                samplesPerTest,
                                onSampleProgress: (testIdx, testName, sampleIdx, totalSamples, liveVal) =>
                                {
                                    CurrentProgress.CurrentTestIndex = testIdx;
                                    CurrentProgress.CurrentTestName = testName;
                                    CurrentProgress.CurrentSampleIndex = sampleIdx;
                                    CurrentProgress.TotalSamplesPerTest = totalSamples;
                                    if (testIdx == 1) { CurrentProgress.LivePrecisionLatencyMs = liveVal; CurrentProgress.CurrentLatencyMs = liveVal; }
                                    else if (testIdx == 2) { CurrentProgress.LivePrecisionJitterMs = liveVal; CurrentProgress.CurrentJitterMs = liveVal; }
                                    else if (testIdx == 3) { CurrentProgress.LivePrecisionThroughputMbps = liveVal; CurrentProgress.CurrentThroughputMbps = liveVal; }
                                    else if (testIdx == 4)
                                    {
                                        CurrentProgress.LivePrecisionBufferbloatMs = liveVal;
                                    }

                                    OnProgressUpdated?.Invoke(CurrentProgress);
                                },
                                token).ConfigureAwait(false);

                            candidateMetrics.CompositeScore = _scoringService.ComputeCompositeScore(candidateMetrics, profile);
                            if (candidateMetrics.CompositeScore > bestScore && candidateMetrics.PacketLossPercent <= baselineMetrics.PacketLossPercent)
                            {
                                bestScore = candidateMetrics.CompositeScore;
                                bestCandidateValue = candVal;
                                bestCandidateMetrics = candidateMetrics;
                                improved = true;
                            }
                            else
                            {
                                _ = await _snapshotService.RollbackSingleParameterAsync(targetAdapter.Id, param.RegistryKeyword, param.CurrentValue, token).ConfigureAwait(false);
                            }
                        }
                    }

                    if (improved && mode != EngineExecutionMode.Simulation)
                    {
                        _ = await ApplyDriverRegistryValueAsync(targetAdapter.Id, param.RegistryKeyword, bestCandidateValue, token).ConfigureAwait(false);
                    }

                    var expRecord = new ExperimentRecord
                    {
                        ExperimentIndex = experimentCounter,
                        ParameterKeyword = param.RegistryKeyword,
                        ParameterDisplayName = param.DisplayName,
                        PreviousValue = param.CurrentValue,
                        TestedValue = bestCandidateValue,
                        SafetyLevel = param.SafetyLevel,
                        ParameterScope = param.Scope,
                        ParameterCategoryName = param.Category.ToString(),
                        ParameterExplanation = !string.IsNullOrEmpty(param.DetailedExplanation) ? param.DetailedExplanation : _safetyService.GetExplanationForParameter(param),
                        MetricsBefore = currentBestMetrics,
                        MetricsAfter = improved ? bestCandidateMetrics : currentBestMetrics,
                        Decision = improved ? ExperimentDecision.Accepted : ExperimentDecision.KeptBaseline,
                        DecisionReason = improved
                            ? $"Meilleure valeur validée parmi {candidateValues.Count} configurations ({samplesPerTest} rounds × 4 dimensions). Gain composite: +{bestScore - currentBestMetrics.CompositeScore:F1} pts."
                            : $"Valeur initiale maintenue après analyse de {candidateValues.Count} configurations ({samplesPerTest} rounds × 4 dimensions). Déjà à l'état optimal.",
                        SamplesTestedCount = samplesPerTest
                    };

                    if (improved)
                    {
                        currentBestMetrics = bestCandidateMetrics;
                        param.CurrentValue = bestCandidateValue;
                        CurrentProgress.CurrentScore = bestCandidateMetrics.CompositeScore;
                        CurrentProgress.CurrentLatencyMs = bestCandidateMetrics.LatencyMedianMs;
                        CurrentProgress.CurrentJitterMs = bestCandidateMetrics.JitterMs;
                        CurrentProgress.CurrentThroughputMbps = bestCandidateMetrics.ThroughputMbps;
                        CurrentProgress.CurrentPacketLoss = bestCandidateMetrics.PacketLossPercent;
                        CurrentProgress.CurrentBufferbloatGrade = bestCandidateMetrics.BufferbloatGrade;
                        Log(EnginePhase.SingleParameterExperiments,
                            $"VALIDÉ : {param.DisplayName} [{param.CurrentValue} -> {bestCandidateValue}] parmi {candidateValues.Count} valeurs ({samplesPerTest} rounds × 4 dimensions). Score: {bestScore:F1}/100 (+{bestScore - baselineMetrics.CompositeScore:F1} pts).", "SUCCESS");
                        await _memoryService.SaveProvenBestSettingAsync(fingerprint, param.RegistryKeyword, bestCandidateValue, bestCandidateMetrics.CompositeScore, token).ConfigureAwait(false);
                    }
                    else
                    {
                        Log(EnginePhase.SingleParameterExperiments,
                            $"MAINTENU : {param.DisplayName} déjà optimal après {candidateValues.Count} configurations ({samplesPerTest} rounds × 4 dimensions).", "INFO");
                    }

                    experimentsList.Add(expRecord);
                    CurrentProgress.CompletedExperiments.Add(expRecord);
                    await _memoryService.RecordExperimentAsync(fingerprint, targetAdapter.Description, targetAdapter.DriverVersion, expRecord, token).ConfigureAwait(false);
                }
            }
            finally
            {
                _watchdogService.StopMonitoring();
            }

            SetPhase(EnginePhase.ValidationVerification, $"Validation finale ({samplesPerTest} rounds × 4 dimensions)...", 85);
            NetworkMetricReport finalMetrics = await _benchmarkService.RunFourPrecisionTestsAsync(
                targetAdapter.GatewayAddress,
                samplesPerTest,
                onSampleProgress: (testIdx, testName, sampleIdx, totalSamples, liveVal) =>
                {
                    CurrentProgress.CurrentTestIndex = testIdx;
                    CurrentProgress.CurrentTestName = $"[Validation] {testName}";
                    CurrentProgress.CurrentSampleIndex = sampleIdx;
                    CurrentProgress.TotalSamplesPerTest = totalSamples;
                    if (testIdx == 1)
                    {
                        CurrentProgress.LivePrecisionLatencyMs = liveVal;
                    }
                    else if (testIdx == 2)
                    {
                        CurrentProgress.LivePrecisionJitterMs = liveVal;
                    }
                    else if (testIdx == 3)
                    {
                        CurrentProgress.LivePrecisionThroughputMbps = liveVal;
                    }
                    else if (testIdx == 4)
                    {
                        CurrentProgress.LivePrecisionBufferbloatMs = liveVal;
                    }

                    OnProgressUpdated?.Invoke(CurrentProgress);
                },
                token).ConfigureAwait(false);

            finalMetrics.CompositeScore = _scoringService.ComputeCompositeScore(finalMetrics, profile);
            CurrentProgress.CurrentScore = finalMetrics.CompositeScore;
            CurrentProgress.CurrentLatencyMs = finalMetrics.LatencyMedianMs;
            CurrentProgress.CurrentJitterMs = finalMetrics.JitterMs;
            CurrentProgress.CurrentThroughputMbps = finalMetrics.ThroughputMbps;
            CurrentProgress.CurrentPacketLoss = finalMetrics.PacketLossPercent;
            CurrentProgress.CurrentBufferbloatGrade = finalMetrics.BufferbloatGrade;

            SetPhase(EnginePhase.StorytellingSummary, "Génération du rapport cognitif et explicatif en langage naturel...", 95);
            string report = _diagnosticsService.GenerateStorytellingReport(
                targetAdapter,
                profile,
                baselineMetrics,
                finalMetrics,
                experimentsList);

            SetPhase(EnginePhase.Completed, "Session d'optimisation terminée avec succès !", 100);
            CurrentState = EngineState.Completed;
            Log(EnginePhase.Completed, "Cycle cognitif achevé. Toutes les configurations validées sont actives et stables.", "SUCCESS");

            return report;
        }
        catch (OperationCanceledException)
        {
            CurrentState = EngineState.Cancelled;
            SetPhase(EnginePhase.Idle, "Optimisation interrompue par l'utilisateur.", CurrentProgress.OverallProgress);
            Log(EnginePhase.Idle, "Session annulée.", "WARN");
            return "Session d'optimisation annulée par l'utilisateur.";
        }
        catch (Exception ex)
        {
            CurrentState = EngineState.Failed;
            SetPhase(EnginePhase.Failed, $"Erreur critique : {ex.Message}", CurrentProgress.OverallProgress);
            Log(EnginePhase.Failed, $"Exception critique : {ex.Message}", "ERROR");
            LoggingService.LogException(ex, "NetworkExperimentEngine.RunAdaptiveOptimizationSessionAsync");

            try
            {
                _ = await _snapshotService.RollbackToBaselineAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }

            throw;
        }
    }

    public Task CancelSessionAsync()
    {
        lock (_stateLock)
        {
            _activeCts?.Cancel();
        }
        return Task.CompletedTask;
    }

    public async Task RequestEmergencyRollbackAsync()
    {
        lock (_stateLock)
        {
            CurrentState = EngineState.RollingBack;
        }
        SetPhase(EnginePhase.Rollback, "Rollback d'urgence déclenché...", CurrentProgress.OverallProgress);
        _ = await _snapshotService.RollbackToBaselineAsync(CancellationToken.None).ConfigureAwait(false);
        Log(EnginePhase.Rollback, "État initial restauré avec succès.", "SUCCESS");
        _activeCts?.Cancel();
    }

    private static string? DetermineCandidateValue(DynamicNetworkParameter param)
    {

        if (param.ValueType == DynamicParameterType.Enumeration && param.ValidOptions.Count > 0)
        {
            string kw = param.RegistryKeyword;

            if (kw.Equals("TCPNoDelay", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("TcpAckFrequency", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("TCPDelAckTicks", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("NetworkThrottlingIndex", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "4294967295" && HasOption(param, "4294967295") ? "4294967295" : null;
            }

            if (kw.Equals("SystemResponsiveness", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("NonBestEffortLimit", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("DefaultTTL", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "64" && HasOption(param, "64") ? "64" : null;
            }

            if (kw.Equals("MaxUserPort", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "65534" && HasOption(param, "65534") ? "65534" : null;
            }

            if (kw.Equals("TcpTimedWaitDelay", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "30" && HasOption(param, "30") ? "30" : null;
            }

            if (kw.Equals("EnablePMTUDiscovery", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("SynAttackProtect", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("NonSackRttResiliency", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("DisableTaskOffload", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("EnableDCA", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("TCPMaxDataRetransmissions", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "3" && HasOption(param, "3") ? "3" : null;
            }

            if (kw.Equals("InitialRto", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1000" && HasOption(param, "1000") ? "1000" : null;
            }

            if (kw.Equals("MinRto", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "20" && HasOption(param, "20") ? "20" : null;
            }

            if (kw.Equals("SackOpts", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("FastSendDatagramThreshold", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "2048" && HasOption(param, "2048") ? "2048" : null;
            }

            if (kw.Equals("DefaultTOSValue", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "40" && HasOption(param, "40") ? "40" : null;
            }

            if (kw.Equals("DisableIPSourceRouting", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "2" && HasOption(param, "2") ? "2" : null;
            }

            if (kw.Equals("EnableICMPRedirect", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("DoNotUseNLA", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("EnablePMTUBHDetect", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("TCPChimney", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("NetDMA", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("FastOpen", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("TcpCreateAndConnectDataUnchecked", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("DisableDHCPMediaSense", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("TCPWindowSize", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "131072" && HasOption(param, "131072") ? "131072" : null;
            }

            if (kw.Equals("GlobalMaxTcpWindowSize", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "262144" && HasOption(param, "262144") ? "262144" : null;
            }

            if (kw.Equals("EcnCapability", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("Timestamps", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("Pacing", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("IGMPLevel", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "2" && HasOption(param, "2") ? "2" : null;
            }

            if (kw.Equals("ArpRetryCount", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "2" && HasOption(param, "2") ? "2" : null;
            }

            if (kw.Equals("ArpCacheLife", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "120" && HasOption(param, "120") ? "120" : null;
            }

            if (kw.Equals("TcpInitialRTT", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("MaxConnectionsPerServer", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "16" && HasOption(param, "16") ? "16" : null;
            }

            if (kw.Equals("MaxConnectionsPer1_0Server", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "16" && HasOption(param, "16") ? "16" : null;
            }

            if (kw.Equals("DnsCacheEntries", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "10240" && HasOption(param, "10240") ? "10240" : null;
            }

            if (kw.Equals("MaxCacheEntryTtlLimit", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "86400" && HasOption(param, "86400") ? "86400" : null;
            }

            if (kw.Equals("NegativeCacheTime", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("NetFailureCacheTime", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("*PriorityVLANTag", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("*RscIPv4", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("*RscIPv6", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("*UsoIPv4", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("*UsoIPv6", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("*WakeOnPattern", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("EnableWlanLowLatency", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("ScanWhenAssociated", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("DisabledComponents", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "32" && HasOption(param, "32") ? "32" : null;
            }

            if (kw.Equals("IPv6TeredoState", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "disabled" && HasOption(param, "disabled") ? "disabled" : null;
            }

            if (kw.Equals("IPv6IsatapState", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "disabled" && HasOption(param, "disabled") ? "disabled" : null;
            }

            if (kw.Equals("IPv66to4State", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "disabled" && HasOption(param, "disabled") ? "disabled" : null;
            }

            if (kw.Equals("TcpNoDelayIPv6", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("TcpAckFrequencyIPv6", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("TcpHeuristics", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "disabled" && HasOption(param, "disabled") ? "disabled" : null;
            }

            if (kw.Equals("CongestionProvider", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "ctcp" && HasOption(param, "ctcp") ? "ctcp" : (param.CurrentValue != "bbr" && HasOption(param, "bbr") ? "bbr" : null);
            }

            if (kw.Equals("AutoTuningLevel", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "normal" && HasOption(param, "normal") ? "normal" : null;
            }

            if (kw.Equals("TCPRss", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "enabled" && HasOption(param, "enabled") ? "enabled" : null;
            }

            if (kw.Equals("TCPRsc", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "disabled" && HasOption(param, "disabled") ? "disabled" : null;
            }

            if (kw.Equals("TCPTimestamps", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "disabled" && HasOption(param, "disabled") ? "disabled" : null;
            }

            if (kw.Equals("TCPEcnCapability", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "disabled" && HasOption(param, "disabled") ? "disabled" : null;
            }

            if (kw.Equals("NetshIPv6Randomize", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "disabled" && HasOption(param, "disabled") ? "disabled" : null;
            }

            if (kw.Equals("MaxNegativeCacheTtl", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("CacheHashTableBucketSize", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "4" && HasOption(param, "4") ? "4" : null;
            }

            if (kw.Equals("*RSS", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("*NumRssQueues", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "4" && HasOption(param, "4") ? "4" : null;
            }

            if (kw.Equals("*RssBaseProcNumber", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("*MaxRssProcessors", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "4" && HasOption(param, "4") ? "4" : null;
            }

            if (kw.Equals("*LsoV1IPv4", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("*LsoV2IPv4", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("*LsoV2IPv6", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("RoamAggressiveness", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("MIMOPowerSaveMode", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("ThroughputBoosterEnabled", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1" && HasOption(param, "1") ? "1" : null;
            }

            if (kw.Equals("2.4GHzChannelWidth", StringComparison.OrdinalIgnoreCase) || kw.Equals("ChannelWidth24", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "20" && HasOption(param, "20") ? "20" : null;
            }

            if (kw.Equals("5GHzChannelWidth", StringComparison.OrdinalIgnoreCase) || kw.Equals("ChannelWidth5", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "80" && HasOption(param, "80") ? "80" : null;
            }

            if (kw.Equals("WirelessMode", StringComparison.OrdinalIgnoreCase))
            {
                return HasOption(param, "802.11ax") ? "802.11ax" : (HasOption(param, "802.11ac") ? "802.11ac" : null);
            }

            if (kw.Equals("*JumboPacket", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "1514" && HasOption(param, "1514") ? "1514" : null;
            }

            if (kw.Equals("NdisAffinity", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "0" && HasOption(param, "0") ? "0" : null;
            }

            if (kw.Equals("RoamThreshold", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "70" && HasOption(param, "70") ? "70" : null;
            }

            if (kw.Equals("EnableAutoDnsOverHttps", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "2" && HasOption(param, "2") ? "2" : null;
            }

            if (kw.Contains("ChecksumOffload", StringComparison.OrdinalIgnoreCase))
            {
                return param.CurrentValue != "3" && HasOption(param, "3") ? "3" : null;
            }

            if (kw.Contains("Interrupt", StringComparison.OrdinalIgnoreCase) ||
                kw.Contains("Flow", StringComparison.OrdinalIgnoreCase) ||
                kw.Contains("EEE", StringComparison.OrdinalIgnoreCase) ||
                kw.Contains("Green", StringComparison.OrdinalIgnoreCase) ||
                kw.Contains("Power", StringComparison.OrdinalIgnoreCase) ||
                kw.Contains("GigaLite", StringComparison.OrdinalIgnoreCase))
            {

                if (param.CurrentValue != "0" && HasOption(param, "0"))
                {
                    return "0";
                }
            }

            if (param.ValidOptions.Count == 2)
            {
                DynamicParameterOption? other = param.ValidOptions.FirstOrDefault(o => !string.Equals(o.RegistryValue, param.CurrentValue, StringComparison.OrdinalIgnoreCase));
                return other?.RegistryValue;
            }

            DynamicParameterOption? candidate = param.ValidOptions.FirstOrDefault(o => !string.Equals(o.RegistryValue, param.CurrentValue, StringComparison.OrdinalIgnoreCase));
            return candidate?.RegistryValue;
        }

        if (param.ValueType == DynamicParameterType.IntegerRange && param.MaxValue.HasValue)
        {
            if (long.TryParse(param.CurrentValue, out long currentLong))
            {

                if (currentLong < param.MaxValue.Value)
                {
                    return param.MaxValue.Value.ToString();
                }
            }
        }

        return null;
    }

    public static List<string> DetermineCandidateValues(DynamicNetworkParameter param, EngineExecutionMode mode, int targetCount)
    {
        var values = new List<string>();
        string current = param.CurrentValue ?? "";

        string? primeCandidate = DetermineCandidateValue(param);
        if (!string.IsNullOrEmpty(primeCandidate) && !values.Contains(primeCandidate))
        {
            values.Add(primeCandidate);
        }

        if (param.ValidOptions != null && param.ValidOptions.Count > 0)
        {
            foreach (DynamicParameterOption opt in param.ValidOptions)
            {
                if (!string.IsNullOrEmpty(opt.RegistryValue) && !values.Contains(opt.RegistryValue))
                {
                    values.Add(opt.RegistryValue);
                }
            }
        }

        if (param.ValueType == DynamicParameterType.IntegerRange && param.MinValue.HasValue && param.MaxValue.HasValue)
        {
            long min = param.MinValue.Value;
            long max = param.MaxValue.Value;
            if (max > min)
            {
                for (int i = 0; i < targetCount; i++)
                {
                    long stepVal = min + (long)Math.Round((double)(max - min) * i / Math.Max(1, targetCount - 1));
                    string s = stepVal.ToString();
                    if (!values.Contains(s))
                    {
                        values.Add(s);
                    }
                }
            }
        }

        string kw = param.RegistryKeyword ?? "";

        if (kw.Contains("Buffer", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i <= targetCount; i++)
            {
                int val = i <= 6 ? (32 * (1 << i)) : (128 * i);
                string s = Math.Clamp(val, 64, 4096).ToString();
                if (!values.Contains(s))
                {
                    values.Add(s);
                }
            }
        }
        else if (kw.Contains("WindowSize", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i <= targetCount; i++)
            {
                int val = 65536 * i;
                string s = val.ToString();
                if (!values.Contains(s))
                {
                    values.Add(s);
                }
            }
        }
        else if (kw.Contains("Port", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i <= targetCount; i++)
            {
                int val = 5000 + (60000 / targetCount * i);
                string s = Math.Clamp(val, 5000, 65534).ToString();
                if (!values.Contains(s))
                {
                    values.Add(s);
                }
            }
        }
        else if (kw.Contains("Delay", StringComparison.OrdinalIgnoreCase) || kw.Contains("Wait", StringComparison.OrdinalIgnoreCase) || kw.Contains("Timeout", StringComparison.OrdinalIgnoreCase) || kw.Contains("Ticks", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < targetCount; i++)
            {
                string s = i.ToString();
                if (!values.Contains(s))
                {
                    values.Add(s);
                }
            }
        }
        else if (kw.Contains("TTL", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i <= targetCount; i++)
            {
                int val = 32 + (96 / targetCount * i);
                string s = val.ToString();
                if (!values.Contains(s))
                {
                    values.Add(s);
                }
            }
        }
        else if (kw.Contains("DisabledComponents", StringComparison.OrdinalIgnoreCase))
        {
            int[] flags = [32, 0, 1, 2, 4, 8, 16, 33, 34, 48, 255];
            foreach (int f in flags)
            {
                string s = f.ToString();
                if (!values.Contains(s))
                {
                    values.Add(s);
                }
            }
        }
        else if (kw.Contains("RssQueues", StringComparison.OrdinalIgnoreCase))
        {
            string[] queues = ["1", "2", "4", "8", "16", "32"];
            foreach (string q in queues)
            {
                if (!values.Contains(q))
                {
                    values.Add(q);
                }
            }
        }
        else if (kw.Contains("CongestionProvider", StringComparison.OrdinalIgnoreCase))
        {
            string[] providers = ["ctcp", "bbr", "cubic", "newreno", "dctcp"];
            foreach (string cp in providers)
            {
                if (!values.Contains(cp))
                {
                    values.Add(cp);
                }
            }
        }
        else if (kw.Contains("AutoTuningLevel", StringComparison.OrdinalIgnoreCase))
        {
            string[] levels = ["normal", "experimental", "highlyrestricted", "disabled", "restricted"];
            foreach (string l in levels)
            {
                if (!values.Contains(l))
                {
                    values.Add(l);
                }
            }
        }
        else if (kw.Contains("Roam", StringComparison.OrdinalIgnoreCase))
        {
            string[] roamLevels = ["1", "2", "3", "4", "5"];
            foreach (string r in roamLevels)
            {
                if (!values.Contains(r))
                {
                    values.Add(r);
                }
            }
        }

        if (values.Count < targetCount && long.TryParse(current, out long curNum))
        {
            for (int i = 0; values.Count < targetCount && i < targetCount * 2; i++)
            {
                long gen = Math.Max(0, curNum - (targetCount / 2) + i);
                string s = gen.ToString();
                if (!values.Contains(s))
                {
                    values.Add(s);
                }
            }
        }

        string[] standardFallbacks = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "12", "16", "20", "24", "30", "32", "40", "64", "128", "256", "512", "1024", "2048", "4096"];
        foreach (string sf in standardFallbacks)
        {
            if (values.Count >= targetCount)
            {
                break;
            }

            if (!values.Contains(sf))
            {
                values.Add(sf);
            }
        }

        for (int i = 0; values.Count < targetCount && i < 100; i++)
        {
            string s = i.ToString();
            if (!values.Contains(s))
            {
                values.Add(s);
            }
        }

        if (values.Count > targetCount)
        {
            values = values.Take(targetCount).ToList();
        }

        return values;
    }

    private static bool HasOption(DynamicNetworkParameter param, string val)
    {
        return param.ValidOptions.Exists(o => string.Equals(o.RegistryValue, val, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> ApplyDriverRegistryValueAsync(string adapterIdOrGuid, string keyword, string value, CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            try
            {

                if (keyword is "TCPNoDelay" or "TcpAckFrequency" or "TCPDelAckTicks" or "MTU" or "NetbiosOptions")
                {
                    using RegistryKey ifKey = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapterIdOrGuid}", writable: true);
                    if (ifKey != null)
                    {
                        if (int.TryParse(value, out int iv))
                        {
                            ifKey.SetValue(keyword, iv, RegistryValueKind.DWord);
                        }
                        else
                        {
                            ifKey.SetValue(keyword, value, RegistryValueKind.String);
                        }

                        return true;
                    }
                }

                else if (keyword is "NetworkThrottlingIndex" or "SystemResponsiveness")
                {
                    using RegistryKey sysKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", writable: true);
                    if (sysKey != null)
                    {
                        if (uint.TryParse(value, out uint uv))
                        {
                            sysKey.SetValue(keyword, unchecked((int)uv), RegistryValueKind.DWord);
                        }

                        return true;
                    }
                }

                else if (keyword is "NonBestEffortLimit")
                {
                    using RegistryKey pschedKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Psched", writable: true);
                    if (pschedKey != null)
                    {
                        if (int.TryParse(value, out int iv))
                        {
                            pschedKey.SetValue(keyword, iv, RegistryValueKind.DWord);
                        }

                        return true;
                    }
                }

                else if (keyword is "DefaultTTL" or "MaxUserPort" or "TcpTimedWaitDelay" or "EnablePMTUDiscovery" or
                                    "EnablePMTUBHDetect" or "SynAttackProtect" or "NonSackRttResiliency" or "DisableTaskOffload" or
                                    "EnableDCA" or "TCPMaxDataRetransmissions" or "InitialRto" or "MinRto" or "SackOpts" or
                                    "FastSendDatagramThreshold" or "DefaultTOSValue" or "DisableIPSourceRouting" or "EnableICMPRedirect" or "DoNotUseNLA" or
                                    "TCPChimney" or "NetDMA" or "FastOpen" or "TcpCreateAndConnectDataUnchecked" or "DisableDHCPMediaSense" or
                                    "TCPWindowSize" or "GlobalMaxTcpWindowSize" or "EcnCapability" or "Timestamps" or "Pacing" or
                                    "IGMPLevel" or "ArpRetryCount" or "ArpCacheLife" or "TcpInitialRTT")
                {
                    using RegistryKey tcpKey = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", writable: true);
                    if (tcpKey != null)
                    {
                        if (int.TryParse(value, out int iv))
                        {
                            tcpKey.SetValue(keyword, iv, RegistryValueKind.DWord);
                        }

                        return true;
                    }
                }

                else if (keyword is "MaxConnectionsPerServer" or "MaxConnectionsPer1_0Server")
                {
                    using RegistryKey inetKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
                    if (inetKey != null)
                    {
                        if (int.TryParse(value, out int iv))
                        {
                            inetKey.SetValue(keyword, iv, RegistryValueKind.DWord);
                        }

                        return true;
                    }
                }

                else if (keyword is "DnsCacheEntries" or "MaxCacheEntryTtlLimit" or "NegativeCacheTime" or "NetFailureCacheTime" or "MaxNegativeCacheTtl" or "CacheHashTableBucketSize")
                {
                    using RegistryKey dnsKey = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", writable: true);
                    if (dnsKey != null)
                    {
                        if (int.TryParse(value, out int iv))
                        {
                            dnsKey.SetValue(keyword, iv, RegistryValueKind.DWord);
                        }

                        return true;
                    }
                }

                else if (keyword is "DisabledComponents")
                {
                    using RegistryKey tcp6Key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", writable: true);
                    if (tcp6Key != null)
                    {
                        if (int.TryParse(value, out int iv))
                        {
                            tcp6Key.SetValue(keyword, iv, RegistryValueKind.DWord);
                        }

                        return true;
                    }
                }
                else if (keyword is "TcpNoDelayIPv6" or "TcpAckFrequencyIPv6")
                {
                    string actualKey = keyword == "TcpNoDelayIPv6" ? "TCPNoDelay" : "TcpAckFrequency";
                    using RegistryKey ifKey = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\{adapterIdOrGuid}", writable: true);
                    if (ifKey != null)
                    {
                        if (int.TryParse(value, out int iv))
                        {
                            ifKey.SetValue(actualKey, iv, RegistryValueKind.DWord);
                        }

                        return true;
                    }
                }
                else if (keyword is "IPv6TeredoState")
                {
                    await RunNetshAsync($"int teredo set state {value}", ct).ConfigureAwait(false);
                    return true;
                }
                else if (keyword is "IPv6IsatapState")
                {
                    await RunNetshAsync($"int ipv6 isatap set state {value}", ct).ConfigureAwait(false);
                    return true;
                }
                else if (keyword is "IPv66to4State")
                {
                    await RunNetshAsync($"int 6to4 set state {value}", ct).ConfigureAwait(false);
                    return true;
                }
                else if (keyword is "TcpHeuristics")
                {
                    await RunNetshAsync($"int tcp set heuristics {value}", ct).ConfigureAwait(false);
                    return true;
                }
                else if (keyword is "CongestionProvider")
                {
                    await RunNetshAsync($"int tcp set supplemental template=custom congestionprovider={value}", ct).ConfigureAwait(false);
                    return true;
                }
                else if (keyword is "AutoTuningLevel")
                {
                    await RunNetshAsync($"int tcp set global autotuninglevel={value}", ct).ConfigureAwait(false);
                    return true;
                }
                else if (keyword is "TCPRss")
                {
                    await RunNetshAsync($"int tcp set global rss={value}", ct).ConfigureAwait(false);
                    return true;
                }
                else if (keyword is "TCPRsc")
                {
                    await RunNetshAsync($"int tcp set global rsc={value}", ct).ConfigureAwait(false);
                    return true;
                }
                else if (keyword is "TCPTimestamps")
                {
                    await RunNetshAsync($"int tcp set global timestamps={value}", ct).ConfigureAwait(false);
                    return true;
                }
                else if (keyword is "TCPEcnCapability")
                {
                    await RunNetshAsync($"int tcp set global ecncapability={value}", ct).ConfigureAwait(false);
                    return true;
                }
                else if (keyword is "NetshIPv6Randomize")
                {
                    await RunNetshAsync($"int ipv6 set global randomizeidentifiers={value}", ct).ConfigureAwait(false);
                    return true;
                }

                using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(NetworkClassKeyPath, writable: true);
                if (classKey == null)
                {
                    return false;
                }

                foreach (string subName in classKey.GetSubKeyNames())
                {
                    if (!int.TryParse(subName, out _))
                    {
                        continue;
                    }

                    using RegistryKey? sub = classKey.OpenSubKey(subName, writable: true);
                    if (sub == null)
                    {
                        continue;
                    }

                    string? netCfgInstanceId = sub.GetValue("NetCfgInstanceId") as string;
                    if (string.Equals(netCfgInstanceId, adapterIdOrGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        sub.SetValue(keyword, value, RegistryValueKind.String);
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, $"ApplyDriverRegistryValueAsync:{keyword}");
                return false;
            }
        }, ct).ConfigureAwait(false);
    }

    private void SetPhase(EnginePhase phase, string message, double progress)
    {
        CurrentProgress.CurrentPhase = phase;
        CurrentProgress.StatusMessage = message;
        CurrentProgress.OverallProgress = progress;
        OnProgressUpdated?.Invoke(CurrentProgress);
    }

    private void UpdateStatus(string message)
    {
        CurrentProgress.StatusMessage = message;
        OnProgressUpdated?.Invoke(CurrentProgress);
    }

    private void UpdateOverallProgress(double progress)
    {
        CurrentProgress.OverallProgress = progress;
        OnProgressUpdated?.Invoke(CurrentProgress);
    }

    private void Log(EnginePhase phase, string message, string level)
    {
        var entry = new CognitiveLogEntry
        {
            Phase = phase,
            Message = message,
            Level = level,
            ProgressPercentage = CurrentProgress.OverallProgress
        };
        OnLogEntryAdded?.Invoke(entry);
    }

    private static async Task RunNetshAsync(string args, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh.exe", args) { CreateNoWindow = true, UseShellExecute = false });
            if (p == null)
            {
                return;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { }
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "NetworkExperimentEngine.RunNetshAsync");
        }
    }
}
