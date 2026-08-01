using System;
using System.IO;
using System.Security.Cryptography;

namespace OpcDaToUaGateway.Services
{
    internal enum TrialStateLoadResult
    {
        Success,
        NotFound,
        Invalid
    }

    internal sealed class TrialStateStore
    {
        private const int FormatVersion = 1;
        private readonly string _filePath;

        internal TrialStateStore(string filePath = null)
        {
            _filePath = filePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "OpcDaToUaGateway",
                "trial.dat");
        }

        internal TrialStateLoadResult Initialize(out long elapsedSeconds)
        {
            TrialStateLoadResult result = TryLoad(out elapsedSeconds);
            if (result != TrialStateLoadResult.NotFound)
                return result;

            if (!TrySave(0))
                return TrialStateLoadResult.Invalid;

            return TryLoad(out elapsedSeconds);
        }

        internal TrialStateLoadResult TryLoad(out long elapsedSeconds)
        {
            elapsedSeconds = 0;
            if (!File.Exists(_filePath))
                return TrialStateLoadResult.NotFound;

            try
            {
                byte[] protectedData = File.ReadAllBytes(_filePath);
                byte[] data = ProtectedData.Unprotect(protectedData, null, DataProtectionScope.LocalMachine);
                using (var stream = new MemoryStream(data, false))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadInt32() != FormatVersion)
                        return TrialStateLoadResult.Invalid;

                    long value = reader.ReadInt64();
                    if (value < 0 || stream.Position != stream.Length)
                        return TrialStateLoadResult.Invalid;

                    elapsedSeconds = value;
                    return TrialStateLoadResult.Success;
                }
            }
            catch
            {
                elapsedSeconds = 0;
                return TrialStateLoadResult.Invalid;
            }
        }

        internal bool TrySave(long elapsedSeconds)
        {
            if (elapsedSeconds < 0)
                return false;

            string directory = Path.GetDirectoryName(_filePath);
            string tempPath = Path.Combine(directory, Path.GetFileName(_filePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(directory);

                byte[] data;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(FormatVersion);
                    writer.Write(elapsedSeconds);
                    writer.Flush();
                    data = stream.ToArray();
                }

                byte[] protectedData = ProtectedData.Protect(data, null, DataProtectionScope.LocalMachine);
                File.WriteAllBytes(tempPath, protectedData);

                if (File.Exists(_filePath))
                    File.Replace(tempPath, _filePath, null);
                else
                    File.Move(tempPath, _filePath);

                return true;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return false;
            }
        }
    }
}
