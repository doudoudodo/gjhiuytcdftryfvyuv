using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Coclico.Installer.Models;

namespace Coclico.Installer.Services;

public static class AiModelDownloader
{
    public static async Task DownloadModelAsync(string installDir, InstallerAiModel model, IProgress<InstallProgressReport> progress, CancellationToken ct = default)
    {
        string modelDir = Path.Combine(installDir, "resource", "model");
        Directory.CreateDirectory(modelDir);
        string targetFile = Path.Combine(modelDir, model.Id);

        // Ajouter l'extension .gguf si elle n'est pas presente
        if (!targetFile.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            targetFile += ".gguf";
        }

        // Si le modèle est déjà présent et valide (hash si connu, sinon taille)
        if (File.Exists(targetFile) && await IsModelFileValidAsync(targetFile, model, ct).ConfigureAwait(false))
        {
            progress.Report(new InstallProgressReport(100, $"Modèle {model.DisplayName} déjà présent.", ""));
            return;
        }

        string tempFile = targetFile + ".tmp";

        // Vérification de l'espace disque avant de lancer un téléchargement
        // de plusieurs Go : échouer immédiatement plutôt qu'à 90 %.
        EnsureSufficientDiskSpace(targetFile, model.SizeMb);

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromHours(2);

        progress.Report(new InstallProgressReport(0, $"Connexion à HuggingFace ({model.DisplayName})...", "Initialisation du téléchargement"));

        // Reprise d'un téléchargement interrompu : si un .tmp partiel existe,
        // on demande au serveur (HTTP Range) la suite à partir de l'octet déjà présent.
        long existingBytes = 0;
        if (File.Exists(tempFile))
        {
            existingBytes = new FileInfo(tempFile).Length;
        }

        long resumeFrom = 0;
        HttpRequestMessage request = new(HttpMethod.Get, model.DownloadUrl);
        if (existingBytes > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (existingBytes > 0 && (int)response.StatusCode == 206)
        {
            // Serveur accepte la reprise : on continue le .tmp existant.
            resumeFrom = existingBytes;
            progress.Report(new InstallProgressReport(0, $"Reprise du téléchargement de {model.DisplayName}...", $"{resumeFrom / (1024 * 1024)} Mo déjà récupérés"));
        }
        else
        {
            // Le serveur ne supporte pas la reprise (200 au lieu de 206) :
            // on repart de zéro.
            resumeFrom = 0;
            try { File.Delete(tempFile); } catch { }
        }

        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength.HasValue
            ? response.Content.Headers.ContentLength.Value + resumeFrom
            : (model.SizeMb * 1024 * 1024);
        long totalRead = resumeFrom;
        if (totalBytes.HasValue && totalRead > totalBytes.Value)
        {
            // .tmp plus gros que le fichier attendu : reprise invalide, on repart de zéro.
            try { File.Delete(tempFile); } catch { }
            resumeFrom = 0;
            totalRead = 0;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using (var fileStream = new FileStream(tempFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, true))
        {
            if (resumeFrom > 0)
            {
                _ = fileStream.Seek(resumeFrom, SeekOrigin.Begin);
            }
            else
            {
                _ = fileStream.SetLength(0);
            }

            byte[] buffer = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;

                double pct = totalBytes > 0 ? ((double)totalRead / totalBytes.Value * 100) : 0;
                string readMb = (totalRead / (1024 * 1024)).ToString();
                string totalMb = ((totalBytes ?? 0) / (1024 * 1024)).ToString();

                progress.Report(new InstallProgressReport(
                    pct,
                    $"Téléchargement de {model.DisplayName} : {readMb} Mo / {totalMb} Mo",
                    $"{pct:F0}%"));
            }
        }

        // Vérification d'intégrité : un GGUF tronqué à 85 % doit être refusé,
        // pas silencieusement accepté par l'heuristique de taille.
        if (string.IsNullOrWhiteSpace(model.ExpectedSha256))
        {
            long downloadedBytes = new FileInfo(tempFile).Length;
            if (downloadedBytes < (model.SizeMb * 1024 * 1024 * 0.8) || downloadedBytes == 0)
            {
                try { File.Delete(tempFile); } catch { }
                throw new InvalidDataException(
                    $"Modèle {model.DisplayName} incomplet : {downloadedBytes} octets téléchargés (taille attendue ~{model.SizeMb} Mo).");
            }
        }
        else
        {
            string actualHash = await ComputeSha256Async(tempFile, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, model.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tempFile); } catch { }
                throw new InvalidDataException(
                    $"Modèle {model.DisplayName} invalide : empreinte SHA-256 incorrecte.");
            }
        }

        if (File.Exists(targetFile))
        {
            try { File.Delete(targetFile); } catch { }
        }
        File.Move(tempFile, targetFile);
    }

    /// <summary>
    /// Refuse le téléchargement si le disque cible n'a pas assez d'espace libre
    /// (taille du modèle + 512 Mo de marge pour la copie temporaire).
    /// </summary>
    private static void EnsureSufficientDiskSpace(string targetFile, long sizeMb)
    {
        try
        {
            string? driveRoot = Path.GetPathRoot(Path.GetFullPath(targetFile));
            if (string.IsNullOrEmpty(driveRoot))
            {
                return;
            }

            var drive = new DriveInfo(driveRoot);
            long requiredBytes = (sizeMb * 1024 * 1024) + (512L * 1024 * 1024);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
            {
                throw new IOException(
                    $"Espace disque insuffisant sur {driveRoot} : {(drive.AvailableFreeSpace / (1024.0 * 1024 * 1024)):F1} Go disponibles, {(requiredBytes / (1024.0 * 1024 * 1024)):F1} Go requis pour le modèle.");
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch
        {
            // DriveInfo indisponible (lecteur réseau, etc.) : on laisse le téléchargement tenter sa chance.
        }
    }

    /// <summary>
    /// An existing model file is trusted only if its SHA-256 matches (when the
    /// catalog pins one), otherwise after the historical size heuristic (> 80 %).
    /// </summary>
    private static async Task<bool> IsModelFileValidAsync(string targetFile, InstallerAiModel model, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(model.ExpectedSha256))
            {
                string hash = await ComputeSha256Async(targetFile, ct).ConfigureAwait(false);
                return string.Equals(hash, model.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
            }

            return new FileInfo(targetFile).Length > (model.SizeMb * 1024 * 1024 * 0.8);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}

