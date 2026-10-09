using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Coclico.Services.AI;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Coclico.Services;

public sealed class AiChatService : IAiService, IDisposable
{
    private readonly SettingsService _settings;
    private readonly MultiProviderClient _multiClient;

    private LLamaWeights? _model;

    private sealed class ChatCtx(LLamaContext ctx) : IDisposable
    {
        public readonly LLamaContext Context = ctx;
        public readonly InteractiveExecutor Executor = new(ctx);
        public void Dispose() { try { Context.Dispose(); } catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); } }
    }

    private volatile ChatCtx? _chatCtx;
    private readonly SemaphoreSlim _chatSem = new(1, 1);

    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private Timer? _idleTimer;
    private int _unloading;

    public AiProviderType CurrentProvider
    {
        get => ResolveSupportedProvider(_settings.Settings.AiProvider);
        set
        {
            _settings.Settings.AiProvider = value.ToString();
            _ = SaveSettingsObservedAsync();
        }
    }

    public string CurrentModel
    {
        get => ResolveSupportedModel(CurrentProvider, _settings.Settings.AiModel);
        set
        {
            _settings.Settings.AiModel = value;
            _ = SaveSettingsObservedAsync();
        }
    }

    public AiChatService(SettingsService settings, MultiProviderClient localClient)
    {
        _settings = settings;
        // Injected by the DI container: a manual 'new' here would silently
        // escape the container with frozen configuration.
        _multiClient = localClient ?? throw new ArgumentNullException(nameof(localClient));
        MigrateLegacyAiSettings();
    }

    /// <summary>
    /// Settings persistence whose failures are logged — a discarded save task
    /// silently lost the user's provider/model choice on immediate close.
    /// </summary>
    private async Task SaveSettingsObservedAsync()
    {
        try
        {
            await _settings.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "AiChatService.SaveSettings");
        }
    }

    private void MigrateLegacyAiSettings()
    {
        bool providerIsSupported = Enum.TryParse(_settings.Settings.AiProvider, out AiProviderType parsedProvider) &&
            Enum.IsDefined(parsedProvider);
        AiProviderType provider = ResolveSupportedProvider(_settings.Settings.AiProvider);

        bool modelIsSupported = string.Equals(
            ResolveSupportedModel(provider, _settings.Settings.AiModel),
            _settings.Settings.AiModel,
            StringComparison.OrdinalIgnoreCase);
        if (modelIsSupported && providerIsSupported)
        {
            return;
        }

        _settings.Settings.AiProvider = provider.ToString();
        _settings.Settings.AiModel = AiModelCatalog.GetDefaultModel(provider).Id;
        _ = SaveSettingsObservedAsync();
    }

    internal static AiProviderType ResolveSupportedProvider(string? savedProvider)
    {
        return Enum.TryParse(savedProvider, out AiProviderType provider) && Enum.IsDefined(provider)
            ? provider
            : AiProviderType.LocalGGUF;
    }

    internal static string ResolveSupportedModel(AiProviderType provider, string? savedModel)
    {
        return AiModelCatalog.GetModelsForProvider(provider)
            .FirstOrDefault(model => string.Equals(model.Id, savedModel, StringComparison.OrdinalIgnoreCase))
            ?.Id ?? AiModelCatalog.GetDefaultModel(provider).Id;
    }

    public static string ModelPath
    {
        get
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resource", "model", "IA-support-chat.gguf");
            if (File.Exists(p))
            {
                return p;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine(baseDir, "..", "..", "..", "..", "Coclico", "bin", "Debug", "net10.0-windows10.0.22621.0", "resource", "model", "IA-support-chat.gguf"),
                Path.Combine(baseDir, "..", "..", "..", "..", "Coclico", "bin", "Release", "net10.0-windows10.0.22621.0", "resource", "model", "IA-support-chat.gguf"),
                Path.Combine(baseDir, "..", "..", "..", "..", "Coclico", "resource", "model", "IA-support-chat.gguf"),
                Path.Combine(baseDir, "..", "..", "..", "..", "Coclico", "publish", "resource", "model", "IA-support-chat.gguf")
            };
            foreach (string? c in candidates)
            {
                string full = Path.GetFullPath(c);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            return p;
        }
    }

    public string GetLocalModelFullPath(string modelFileName)
    {
        string primary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resource", "model", modelFileName);
        if (File.Exists(primary))
        {
            return primary;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "Coclico", "bin", "Debug", "net10.0-windows10.0.22621.0", "resource", "model", modelFileName),
            Path.Combine(baseDir, "..", "..", "..", "..", "Coclico", "bin", "Release", "net10.0-windows10.0.22621.0", "resource", "model", modelFileName),
            Path.Combine(baseDir, "..", "..", "..", "..", "Coclico", "resource", "model", modelFileName),
            Path.Combine(baseDir, "..", "..", "..", "..", "Coclico", "publish", "resource", "model", modelFileName)
        };
        foreach (string? c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return primary;
    }

    private static readonly string DocsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "resource", "docs");

    private static readonly Lazy<RagService> _rag = new(() =>
    {
        var r = new RagService();
        r.BuildIndex(DocsPath);
        return r;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _memLock = new();
    private readonly List<(string User, string Ai)> _shortTermMemory = [];
    private const int MaxMemoryTurns = 5;
    private int _turnsSinceReset = 0;
    private const int AutoResetAfterTurns = 10;

    private static int OptimalThreads =>
        Math.Max(2, Math.Min(Environment.ProcessorCount - 1, 6));

    public bool IsEnabled => _settings.Settings.AiEnabled;
    public bool UseGpu => _settings.Settings.AiUseGpu;

    public async Task SetEnabledAsync(bool enabled)
    {
        _settings.Settings.AiEnabled = enabled;
        await _settings.SaveAsync().ConfigureAwait(false);
        if (!enabled)
        {
            await UnloadLocalModelAsync().ConfigureAwait(false);
            _ = MemoryCleanerService.ForceGcCollect();
            LoggingService.LogInfo("[AiChatService] Copilot désactivé : mémoire libérée (0 Mo RAM/VRAM).");
        }
    }

    public async Task SetHardwareModeAsync(bool useGpu, int layers = 99)
    {
        _settings.Settings.AiUseGpu = useGpu;
        _settings.Settings.AiGpuLayers = Math.Clamp(layers, 0, 128);
        await _settings.SaveAsync().ConfigureAwait(false);

        if (_initialized && CurrentProvider == AiProviderType.LocalGGUF)
        {
            await UnloadLocalModelAsync().ConfigureAwait(false);
            await InitializeAsync().ConfigureAwait(false);
        }
        LoggingService.LogInfo($"[AiChatService] Mode matériel changé : {(useGpu ? $"GPU ({_settings.Settings.AiGpuLayers} layers)" : "CPU pur")}");
    }

    private static readonly string[] TurnStopTokens = [
        "<|eot_id|>",
        "<|end_of_text|>",
        "<|im_end|>",
        "<|end|>",
        "<|endoftext|>",
        "<|start_header_id|>",
        "<|end_header_id|>",
        "<|im_start|>",
        "<|user|>",
        "<|assistant|>",
        "\nUser:",
        "\nAssistant:",
        "\nUtilisateur:",
        "\nAssistant :"
    ];

    private const string SystemPrompt =
        "Tu es Coclico Copilot, l'assistant intelligent officiel de Coclico — gestionnaire système haute performance pour Windows.\n" +
        "Tu es courtois, direct, technique, bienveillant et concis. Tu réponds toujours en français.\n\n" +
        "MODULES DE COCLICO :\n" +
        "- Tableau de bord : Vue d'ensemble des ressources système.\n" +
        "- Applications : Catalogue et gestion des logiciels installés.\n" +
        "- RAM Cleaner : Optimisation poussée de la mémoire vive (Working sets, Standby list, GC compact).\n" +
        "- Nettoyage Système : Suppression sécurisée des fichiers temporaires, caches et corbeille.\n" +
        "- Santé & Défense : Réparation système (SFC, DISM), sécurité Windows Defender, remise à neuf.\n" +
        "- Installeur : Gestionnaire de logiciels et mises à jour Winget.\n" +
        "- Paramètres : Options système, accélération matérielle, coffre DPAPI.\n\n" +
        "CONSIGNES STRICTES DE COMPORTEMENT :\n" +
        "1. Salutations, politesses et questions amicales ('bonjour', 'salut', 'comment vas-tu', 'commetn vas tyu', 'ça va', 'merci', etc.) :\n" +
        "   - Réponds TOUJOURS avec courtoisie, chaleur et brièveté (ex: \"Bonjour ! Je vais très bien, merci. Comment puis-je vous aider aujourd'hui sur votre PC ?\").\n" +
        "   - Sois tolérant aux fautes d'orthographe ou de frappe courantes ('commetn vas tyu' = comment vas-tu).\n" +
        "   - Ne dis JAMAIS que tu ne comprends pas une salutation ou une question de politesse.\n" +
        "   - N'inclus JAMAIS de balise d'action pour une salutation.\n" +
        "2. Vérité et métriques :\n" +
        "   - N'invente JAMAIS de fausses statistiques ou de faux pourcentages système (CPU/RAM inventés). Ne liste pas de métriques système sauf si l'utilisateur demande explicitement un diagnostic ou des métriques.\n" +
        "3. EXÉCUTION D'ACTIONS SYSTÈME (RÈGLE ABSOLUE) :\n" +
        "   - Dès que l'utilisateur te demande d'effectuer une action (nettoyer la RAM, vider les temporaires, lancer un scan SFC/DISM/Defender, diagnostic, etc.), tu DOIS OBLIGATOIREMENT insérer la balise d'action correspondante [ACTION:TYPE] dans ta réponse !\n" +
        "   - RÈGLE DE RÉPONSE POUR LES ACTIONS (STRICTE) :\n" +
        "     * Ton message texte doit être TRÈS COURT (UNE SEULE PHRASE) confirmant le lancement (ex: \"Parfait, je lance l'optimisation de la mémoire vive pour vous. [ACTION:CLEAN_RAM]\").\n" +
        "     * INTERDICTION STRICTE de détailler de fausses étapes (JAMAIS de 'Étape 1', 'Étape 2', 'Vérification en cours...', ni de listes à puces simulant des processus).\n" +
        "     * INTERDICTION STRICTE d'écrire que l'action est terminée (JAMAIS de 'Nettoyage terminé !', 'Action terminée', 'Succès' ou faux chiffres dans ton texte) : l'interface graphique Coclico affiche déjà une carte dédiée avec le statut 'En cours...' puis le résultat réel une fois terminé.\n" +
        "     * Ne demande jamais à l'utilisateur de redémarrer son PC.\n" +
        "   - Sans cette balise entre crochets, l'action système ne s'exécute pas. Insère-la systématiquement dans ta réponse.\n" +
        "   - Balises disponibles :\n" +
        "     * Nettoyage RAM : [ACTION:CLEAN_RAM]\n" +
        "     * Démon RAM automatique : [ACTION:CONFIGURE_RAM_DAEMON <parametres>]\n" +
        "     * Profil Mémoire RAM : [ACTION:SET_RAM_PROFILE <Smart|Gamer|Extreme|Aggressive>]\n" +
        "     * Optimisation Réseau & Cartes : [ACTION:OPTIMIZE_NETWORK <gaming|quick|throughput|autotune>]\n" +
        "     * Benchmark Réseau & DNS : [ACTION:BENCHMARK_NETWORK]\n" +
        "     * Instantané réseau (sauvegarde) : [ACTION:SNAPSHOT_NETWORK]\n" +
        "     * Restauration instantané réseau : [ACTION:RESTORE_NETWORK_SNAPSHOT]\n" +
        "     * Restauration Réseau Défaut : [ACTION:RESET_NETWORK_DEFAULTS]\n" +
        "     * Analyse Crashs & Écran Bleu (BSOD) : [ACTION:ANALYZE_CRASHES]\n" +
        "     * Nettoyage Fichiers Temporaires : [ACTION:CLEAN_TEMP]\n" +
        "     * Scan intégrité SFC : [ACTION:SCAN_SFC]\n" +
        "     * Réparation image DISM : [ACTION:SCAN_DISM]\n" +
        "     * Analyse antivirus Defender : [ACTION:DEFENDER_SCAN]\n" +
        "     * Réparation intégrale Windows : [ACTION:FULL_REPAIR]\n" +
        "     * Réinitialisation Réseau & DNS : [ACTION:RESET_NETWORK]\n" +
        "     * Réinitialisation Windows Update : [ACTION:RESET_UPDATE]\n" +
        "     * Mise à jour de toutes les applications : [ACTION:UPDATE_APPS]\n" +
        "     * Diagnostic complet en direct : [ACTION:DIAGNOSE_SYSTEM]\n" +
        "     * Navigation module : [ACTION:NAVIGATE <dashboard|programs|ram|network|cleaning|disk|installer|health|settings|help>]\n" +
        "     * Lancer un logiciel : [ACTION:OPEN_APP <nom>]\n" +
        "     * Installer via Winget : [ACTION:INSTALL_WINGET <id>]\n" +
        "   - CORRESPONDANCE DES DEMANDES COURANTES (tolère les fautes de frappe et l'argot) :\n" +
        "     * 'libère de la mémoire', 'mon PC est lent', 'vide la RAM' => [ACTION:CLEAN_RAM]\n" +
        "     * 'nettoie mon PC', 'supprime les fichiers temporaires', 'vide les caches' => [ACTION:CLEAN_TEMP]\n" +
        "     * 'accélère internet', 'baisse mon ping', 'optimise le wifi', 'améliore ma connexion' => [ACTION:OPTIMIZE_NETWORK gaming]\n" +
        "     * 'teste ma connexion', 'quel est mon ping', 'teste les DNS' => [ACTION:BENCHMARK_NETWORK]\n" +
        "     * 'sauvegarde ma config réseau', 'fais un instantané du réseau' => [ACTION:SNAPSHOT_NETWORK]\n" +
        "     * 'annule la dernière optimisation réseau', 'restaure le réseau d'avant' => [ACTION:RESTORE_NETWORK_SNAPSHOT]\n" +
        "     * 'mets à jour mes applications', 'mets tout à jour' => [ACTION:UPDATE_APPS]\n" +
        "     * 'répare Windows', 'vérifie les fichiers système' => [ACTION:SCAN_SFC]\n" +
        "     * 'scan antivirus', 'vérifie les virus' => [ACTION:DEFENDER_SCAN]\n" +
        "     * 'fais un diagnostic', 'comment va mon PC', 'état du système' => [ACTION:DIAGNOSE_SYSTEM]\n" +
        "     * 'analyse le disque', 'qu'est-ce qui prend de la place', 'montre l'espace disque' => [ACTION:NAVIGATE disk]\n" +
        "   - Si la demande est ambiguë ou ne correspond à aucune balise, utilise [ACTION:NAVIGATE <module>] pour ouvrir le module le plus proche du besoin de l'utilisateur plutôt que de ne rien faire.\n" +
        "   - IMPORTANT : N'émets qu'UNE SEULE balise par action demandée. N'invente JAMAIS de sous-catégories inexistantes (comme TYPE_A ou TYPE_B). Ne demande pas si l'utilisateur veut continuer : confirme simplement et lance l'action immédiatement.\n" +
        "4. Forme :\n" +
        "   - Limite-toi STRICTEMENT à une seule réponse directe. Ne génère jamais de dialogue fictif futur ni de balises de fin de tour.";

    public string CurrentStatusContext { get; set; } = "Tableau de Bord";

    public static bool IsModelAvailable => File.Exists(ModelPath);
    public bool IsModelDownloaded(string? modelId = null)
    {
        string id = modelId ?? CurrentModel;
        string path = GetLocalModelFullPath(id);
        return File.Exists(path) || (id == "IA-support-chat.gguf" && File.Exists(ModelPath));
    }

    public bool IsInitialized => CurrentProvider != AiProviderType.LocalGGUF || _initialized;
    public int ActiveGpuLayers { get; private set; } = 0;

    public async Task SwitchProviderAsync(AiProviderType provider, string? model = null)
    {
        AiProviderType oldProvider = CurrentProvider;
        string oldModel = CurrentModel;
        CurrentProvider = provider;

        CurrentModel = !string.IsNullOrWhiteSpace(model) ? model : AiModelCatalog.GetDefaultModel(provider).Id;

        if (oldProvider == AiProviderType.LocalGGUF &&
            (provider != AiProviderType.LocalGGUF || !string.Equals(oldModel, CurrentModel, StringComparison.OrdinalIgnoreCase)))
        {
            await UnloadLocalModelAsync().ConfigureAwait(false);
        }
        else if (oldProvider != AiProviderType.LocalGGUF && provider == AiProviderType.LocalGGUF)
        {
            _initialized = false;
        }

        ResetConversation();
        LoggingService.LogInfo($"[AiChatService] Fournisseur changé pour {provider} (modèle: {CurrentModel})");
    }

    public async Task UnloadAsync()
    {
        await UnloadLocalModelAsync().ConfigureAwait(false);
    }

    private static bool _nativeCudaConfigured = false;
    private static string? _cudaLlamaDllPath = null;
    private static readonly object _cudaConfigLock = new();

    public static void EnsureNativeBackendConfigured(bool useGpu)
    {
        lock (_cudaConfigLock)
        {
            if (!useGpu)
            {
                try
                {
                    _ = LLama.Native.NativeLibraryConfig.All.WithCuda(false).WithAutoFallback(true);
                }
                catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
                return;
            }

            if (_nativeCudaConfigured)
            {
                if (!string.IsNullOrEmpty(_cudaLlamaDllPath))
                {
                    _ = LLama.Native.NativeLibraryConfig.LLama.WithLibrary(_cudaLlamaDllPath);
                }
                _ = LLama.Native.NativeLibraryConfig.All
                    .WithCuda(true)
                    .SkipCheck(true)
                    .WithAutoFallback(false);
                return;
            }

            try
            {
                string baseDir = AppContext.BaseDirectory;
                var candidates = new List<string>
                {
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    Path.Combine(baseDir, "native"),
                    baseDir
                };

                try
                {
                    var dir = new DirectoryInfo(baseDir);
                    for (int i = 0; i < 5 && dir?.Parent != null; i++)
                    {
                        dir = dir.Parent;
                        candidates.Add(Path.Combine(dir.FullName, "Coclico", "bin", "Debug", "net10.0-windows10.0.22621.0", "runtimes", "win-x64", "native"));
                        candidates.Add(Path.Combine(dir.FullName, "Coclico", "bin", "Release", "net10.0-windows10.0.22621.0", "runtimes", "win-x64", "native"));
                        candidates.Add(Path.Combine(dir.FullName, "runtimes", "win-x64", "native"));
                    }
                }
                catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }

                string nativeDir = candidates.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "cuda12")))
                                ?? candidates.FirstOrDefault(Directory.Exists)
                                ?? Path.Combine(baseDir, "runtimes", "win-x64", "native");

                string cuda12Dir = Path.Combine(nativeDir, "cuda12");
                string avx2Dir = Path.Combine(nativeDir, "avx2");

                string ResolveDll(string fileName)
                {
                    string direct = Path.Combine(nativeDir, fileName);
                    if (File.Exists(direct))
                    {
                        return direct;
                    }

                    foreach (string c in candidates)
                    {
                        string p = Path.Combine(c, fileName);
                        if (File.Exists(p))
                        {
                            return p;
                        }
                    }
                    return direct;
                }

                if (!Directory.Exists(cuda12Dir))
                {
                    // FALLBACK GRACEFUL : si CUDA n'est pas disponible, on passe automatiquement en mode CPU
                    LoggingService.LogInfo("[AiChatService] CUDA 12 non trouvé, fallback vers AVX2/CPU...");
                    _ = LLama.Native.NativeLibraryConfig.All.WithCuda(false).WithAutoFallback(true);
                    return;
                }

                string curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!curPath.Contains(cuda12Dir, StringComparison.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("PATH", $"{cuda12Dir};{nativeDir};{avx2Dir};{curPath}");
                }

                TryPreloadDll(ResolveDll("cudart64_12.dll"));
                TryPreloadDll(ResolveDll("cublasLt64_12.dll"));
                TryPreloadDll(ResolveDll("cublas64_12.dll"));
                TryPreloadDll(ResolveDll("cufft64_11.dll"));
                TryPreloadDll(ResolveDll("nvrtc64_120_0.dll"));
                TryPreloadDll(Path.Combine(cuda12Dir, "ggml-base.dll"));
                TryPreloadDll(Path.Combine(avx2Dir, "ggml-cpu.dll"));
                TryPreloadDll(Path.Combine(cuda12Dir, "ggml-cuda.dll"));
                TryPreloadDll(Path.Combine(cuda12Dir, "ggml.dll"));

                string llamaCuda = Path.Combine(cuda12Dir, "llama.dll");
                if (!File.Exists(llamaCuda))
                {
                    throw new FileNotFoundException($"Bibliothèque llama.dll CUDA 12 introuvable : '{llamaCuda}'");
                }

                TryPreloadDll(llamaCuda);
                _cudaLlamaDllPath = llamaCuda;
                _ = LLama.Native.NativeLibraryConfig.LLama.WithLibrary(llamaCuda);
                _ = LLama.Native.NativeLibraryConfig.All
                    .WithCuda(true)
                    .SkipCheck(true)
                    .WithAutoFallback(false);

                LLama.Native.NativeApi.llama_empty_call();
                _nativeCudaConfigured = true;
                LoggingService.LogInfo("[AiChatService] Backend NVIDIA CUDA 12 initialisé avec succès.");
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "AiChatService.EnsureNativeBackendConfigured");
                throw new InvalidOperationException($"Impossible d'initialiser le backend GPU CUDA 12 : {ex.Message}. Vérifiez vos pilotes NVIDIA et vos dépendances matérielles.", ex);
            }
        }
    }

    private static void TryPreloadDll(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            _ = System.Runtime.InteropServices.NativeLibrary.Load(path);
        }
        catch (Exception ex)
        {
            LoggingService.LogDebug($"[AiChatService] Préchargement DLL '{path}' : {ex.Message}");
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default(CancellationToken))
    {
        if (!_settings.Settings.AiEnabled)
        {
            LoggingService.LogInfo("[AiChatService] Initialisation ignorée : Copilot désactivé par l'utilisateur.");
        }
        else if (CurrentProvider != AiProviderType.LocalGGUF)
        {
            _initialized = true;
        }
        else
        {
            if (_initialized)
            {
                return;
            }
            await _initLock.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
            try
            {
                if (_initialized)
                {
                    return;
                }
                string targetPath = GetLocalModelFullPath(CurrentModel);
                if (!File.Exists(targetPath))
                {
                    if (CurrentModel == "IA-support-chat.gguf" && File.Exists(ModelPath))
                    {
                        targetPath = ModelPath;
                    }
                    else
                    {
                        IReadOnlyList<AiModelInfo> modelsForProvider = AiModelCatalog.GetModelsForProvider(AiProviderType.LocalGGUF);
                        AiModelInfo? aiModelInfo = modelsForProvider.FirstOrDefault(m => File.Exists(GetLocalModelFullPath(m.Id)));
                        if (!(aiModelInfo != null))
                        {
                            throw new FileNotFoundException("Le modèle local '" + CurrentModel + "' n'est pas encore téléchargé. Téléchargez-le depuis la bannière.");
                        }
                        CurrentModel = aiModelInfo.Id;
                        targetPath = GetLocalModelFullPath(aiModelInfo.Id);
                    }
                }
                bool aiUseGpu = _settings.Settings.AiUseGpu;
                int targetLayers = (aiUseGpu ? Math.Clamp((_settings.Settings.AiGpuLayers > 0) ? _settings.Settings.AiGpuLayers : 32, 1, 99) : 0);
                if (aiUseGpu)
                {
                    try
                    {
                        EnsureNativeBackendConfigured(useGpu: true);
                        await LoadModelAsync(targetPath, targetLayers, ct).ConfigureAwait(continueOnCapturedContext: false);
                        ActiveGpuLayers = targetLayers;
                        _initialized = true;
                        LoggingService.LogInfo($"[AiChatService] Modèle local chargé sur GPU NVIDIA ({targetLayers} couches en VRAM) : {targetPath}");
                        ResetIdleTimer();
                        return;
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning("[AiChatService] Échec du chargement sur GPU (" + ex.Message + "). Basculement automatique sur CPU...");
                        DisposeModelInternal();
                        EnsureNativeBackendConfigured(useGpu: false);
                        await LoadModelAsync(targetPath, 0, ct).ConfigureAwait(continueOnCapturedContext: false);
                        ActiveGpuLayers = 0;
                        _initialized = true;
                        LoggingService.LogInfo("[AiChatService] Modèle local chargé sur CPU (repli réussi) : " + targetPath);
                        ResetIdleTimer();
                        return;
                    }
                }
                EnsureNativeBackendConfigured(useGpu: false);
                await LoadModelAsync(targetPath, 0, ct).ConfigureAwait(continueOnCapturedContext: false);
                ActiveGpuLayers = 0;
                _initialized = true;
                LoggingService.LogInfo("[AiChatService] Modèle local chargé sur CPU pur (0 couche VRAM) : " + targetPath);
                ResetIdleTimer();
            }
            finally
            {
                _initLock.Release();
            }
        }
    }

    private Task LoadModelAsync(string modelPath, int gpuLayers, CancellationToken ct)
    {
        ModelParams p = BuildModelParams(modelPath, gpuLayers);
        return Task.Run(() =>
        {
            _model = LLamaWeights.LoadFromFile(p);
            Interlocked.Exchange(ref _chatCtx, new ChatCtx(_model.CreateContext(p)))?.Dispose();
        }, ct);
    }

    private static ModelParams BuildModelParams(string modelPath, int gpuLayers)
    {
        return new ModelParams(modelPath)
        {
            ContextSize = 2048u,
            GpuLayerCount = gpuLayers,
            MainGpu = 0,
            SplitMode = GPUSplitMode.None,
            Threads = ((gpuLayers > 0) ? 4 : OptimalThreads),
            BatchSize = 512u,
            UseMemorymap = (gpuLayers <= 0),
            FlashAttention = false
        };
    }

    public IAsyncEnumerable<string> SendMessageAsync(string userMessage, CancellationToken ct = default(CancellationToken))
    {
        return SendMessageWithVisionAsync(userMessage, null, null, ct);
    }

    public async IAsyncEnumerable<string> SendMessageWithVisionAsync(string userMessage, string? base64Image = null, string? imageMimeType = null, [EnumeratorCancellation] CancellationToken ct = default(CancellationToken))
    {
        if (string.IsNullOrEmpty(base64Image) && AiToolExecutionService.TryGetConversationalResponse(userMessage, out string? response))
        {
            string[] words = response.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return (i == 0) ? words[i] : (" " + words[i]);
                await Task.Delay(12, ct).ConfigureAwait(continueOnCapturedContext: false);
            }
            yield break;
        }
        string systemWithContext = BuildEnrichedSystemPrompt(userMessage);
        if (CurrentProvider == AiProviderType.Ollama)
        {
            List<(string User, string Ai)> historyCopy;
            lock (_memLock)
            {
                historyCopy = _shortTermMemory.ToList();
            }
            await foreach (string token in _multiClient.StreamChatAsync(
                CurrentModel,
                _settings.Settings.OllamaEndpoint,
                systemWithContext,
                historyCopy,
                userMessage,
                base64Image,
                ct).ConfigureAwait(false))
            {
                yield return token;
            }
            yield break;
        }
        if (!_initialized)
        {
            await InitializeAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
        }
        await _chatSem.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            ResetContextOnlyInternal();
            ChatCtx? chatCtx = _chatCtx;
            if (chatCtx == null)
            {
                yield break;
            }
            string text2 = BuildLocalPrompt(userMessage);
            InferenceParams inferenceParams = new InferenceParams
            {
                MaxTokens = 512,
                AntiPrompts = TurnStopTokens,
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.25f,
                    TopP = 0.9f,
                    TopK = 40,
                    RepeatPenalty = 1.15f
                }
            };
            StringBuilder rollingBuffer = new StringBuilder();
            await foreach (string item in chatCtx.Executor.InferAsync(text2, inferenceParams, ct).ConfigureAwait(continueOnCapturedContext: false))
            {
                int num = -1;
                string[] turnStopTokens = TurnStopTokens;
                foreach (string value in turnStopTokens)
                {
                    int num2 = item.IndexOf(value, StringComparison.OrdinalIgnoreCase);
                    if (num2 >= 0 && (num < 0 || num2 < num))
                    {
                        num = num2;
                    }
                }
                if (num >= 0)
                {
                    string text3 = item.Substring(0, num);
                    if (!string.IsNullOrEmpty(text3))
                    {
                        yield return text3;
                    }
                    break;
                }
                rollingBuffer.Append(item);
                string text4 = rollingBuffer.ToString();
                num = -1;
                string[] turnStopTokens2 = TurnStopTokens;
                foreach (string value2 in turnStopTokens2)
                {
                    int num3 = text4.IndexOf(value2, StringComparison.OrdinalIgnoreCase);
                    if (num3 >= 0 && (num < 0 || num3 < num))
                    {
                        num = num3;
                    }
                }
                if (num >= 0)
                {
                    break;
                }
                yield return item;
            }
        }
        finally
        {
            _chatSem.Release();
            ResetIdleTimer();
        }
    }

    private string BuildEnrichedSystemPrompt(string userMessage)
    {
        var sb = new StringBuilder(SystemPrompt);

        if (!AiToolExecutionService.IsCasualOrGreeting(userMessage) && !string.IsNullOrWhiteSpace(CurrentStatusContext))
        {
            _ = sb.AppendLine($"\n[Contexte navigation : l'utilisateur se trouve dans le module '{CurrentStatusContext}' de Coclico. Ne mentionne ce contexte que si l'utilisateur demande explicitement une aide sur le module actuel.]");
        }

        if (!AiToolExecutionService.IsCasualOrGreeting(userMessage) && userMessage.Length >= 12)
        {
            string knowledge = GetKnowledge(userMessage);
            if (!string.IsNullOrWhiteSpace(knowledge))
            {
                _ = sb.AppendLine($"\n[Documentation technique pertinente]\n{knowledge}");
            }
        }

        return sb.ToString();
    }

    private string BuildLocalPrompt(string userMessage)
    {
        string modelName = CurrentModel.ToLowerInvariant();
        bool isQwen = modelName.Contains("qwen");

        var sb = new StringBuilder();
        string sys = BuildEnrichedSystemPrompt(userMessage);

        if (isQwen)
        {
            _ = sb.Append("<|im_start|>system\n").Append(sys).Append("<|im_end|>\n");

            lock (_memLock)
            {
                foreach ((string? u, string? a) in _shortTermMemory)
                {
                    _ = sb.Append("<|im_start|>user\n").Append(u).Append("<|im_end|>\n");
                    _ = sb.Append("<|im_start|>assistant\n").Append(a).Append("<|im_end|>\n");
                }
            }

            _ = sb.Append("<|im_start|>user\n").Append(userMessage).Append("<|im_end|>\n<|im_start|>assistant\n");
        }
        else
        {
            _ = sb.Append("<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n");
            _ = sb.Append(sys);
            _ = sb.Append("<|eot_id|>");

            lock (_memLock)
            {
                foreach ((string? u, string? a) in _shortTermMemory)
                {
                    _ = sb.Append("<|start_header_id|>user<|end_header_id|>\n\n");
                    _ = sb.Append(u);
                    _ = sb.Append("<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n");
                    _ = sb.Append(a);
                    _ = sb.Append("<|eot_id|>");
                }
            }

            _ = sb.Append("<|start_header_id|>user<|end_header_id|>\n\n");
            _ = sb.Append(userMessage);
            _ = sb.Append("<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n");
        }

        return sb.ToString();
    }

    public void RecordExchange(string userMsg, string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
        {
            return;
        }

        bool shouldReset = false;
        lock (_memLock)
        {
            _shortTermMemory.Add((userMsg.Trim(), aiResponse.Trim()));
            if (_shortTermMemory.Count > MaxMemoryTurns)
            {
                _shortTermMemory.RemoveAt(0);
            }

            _turnsSinceReset++;
            if (_turnsSinceReset >= AutoResetAfterTurns)
            {
                _turnsSinceReset = 0;
                shouldReset = true;
            }
        }

        if (shouldReset)
        {
            ResetContextOnly();
        }
    }

    private string BuildMemoryBlock()
    {
        lock (_memLock)
        {
            if (_shortTermMemory.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder("[Mémoire récente]\n");
            for (int i = 0; i < _shortTermMemory.Count; i++)
            {
                (string? user, string? ai) = _shortTermMemory[i];
                string u = user.Length > 100 ? user[..100] + "…" : user;
                string a = ai.Length > 150 ? ai[..150] + "…" : ai;
                _ = sb.AppendLine($"T{i + 1} U:\"{u}\" A:\"{a}\"");
            }
            return sb.ToString().Trim();
        }
    }

    private static string GetKnowledge(string query)
    {
        return !Directory.Exists(DocsPath) ? string.Empty : _rag.Value.Search(query, topK: 2, maxChars: 350) ?? string.Empty;
    }

    public void ResetConversation()
    {
        lock (_memLock)
        {
            _shortTermMemory.Clear();
            _turnsSinceReset = 0;
        }
        ResetContextOnly();
    }

    public void ResetContextOnly()
    {
        _chatSem.Wait();
        try
        {
            ResetContextOnlyInternal();
        }
        finally
        {
            _ = _chatSem.Release();
        }
    }

    private void ResetContextOnlyInternal()
    {
        if (_model == null)
        {
            return;
        }

        string targetPath = GetLocalModelFullPath(CurrentModel);
        if (!File.Exists(targetPath))
        {
            targetPath = ModelPath;
        }

        var newCtx = new ChatCtx(_model.CreateContext(BuildModelParams(targetPath, ActiveGpuLayers)));
        Interlocked.Exchange(ref _chatCtx, newCtx)?.Dispose();
    }

    private void ResetIdleTimer()
    {
        int minutes = _settings.Settings.AiIdleTimeoutMinutes;
        if (minutes <= 0)
        {
            _ = (_idleTimer?.Change(Timeout.Infinite, Timeout.Infinite));
            return;
        }

        int ms = minutes * 60_000;
        if (_idleTimer is null)
        {
            _idleTimer = new Timer(OnIdleTimeout, null, ms, Timeout.Infinite);
        }
        else
        {
            _ = _idleTimer.Change(ms, Timeout.Infinite);
        }
    }

    private void OnIdleTimeout(object? state)
    {
        if (Interlocked.CompareExchange(ref _unloading, 1, 0) != 0)
        {
            return;
        }

        // Timer callbacks run on a thread-pool thread: fire the async unload
        // without blocking it (no sync-over-async deadlock risk).
        _ = UnloadOnIdleTimeoutAsync();
    }

    private async Task UnloadOnIdleTimeoutAsync()
    {
        try
        {
            await UnloadLocalModelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "AiChatService.OnIdleTimeout");
        }
        finally
        {
            _ = Interlocked.Exchange(ref _unloading, 0);
        }
    }

    private async Task UnloadLocalModelAsync()
    {
        if (!_initialized)
        {
            return;
        }

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_model == null)
            {
                return;
            }

            await _chatSem.WaitAsync().ConfigureAwait(false);
            try
            {
                DisposeModelInternal();
                _initialized = false;
                LoggingService.LogInfo("[AiChatService] Modèle local déchargé de la mémoire (0 Mo RAM)");
            }
            finally { _ = _chatSem.Release(); }

            _ = MemoryCleanerService.ForceGcCollect();
        }
        finally
        {
            _ = _initLock.Release();
        }
    }

    private void DisposeModelInternal()
    {
        Interlocked.Exchange(ref _chatCtx, null)?.Dispose();
        _model?.Dispose(); _model = null;
    }

    public void Dispose()
    {
        _idleTimer?.Dispose();
        DisposeModelInternal();
        _initLock.Dispose();
        _chatSem.Dispose();
        GC.SuppressFinalize(this);
    }
}
