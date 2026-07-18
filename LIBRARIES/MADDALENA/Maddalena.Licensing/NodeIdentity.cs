namespace Maddalena.Licensing;

/// <summary>
/// The node's identity: an install GUID written once into the data folder and stable thereafter. It is the
/// <c>nodeId</c> an activation binds to. It survives restarts and VM migrations (it is a file in the data,
/// not a hardware id that would break when legitimate infrastructure changes).
/// </summary>
public static class NodeIdentity
{
    private const string FileName = "node-id";

    /// <summary>
    /// Returns the install GUID from the <paramref name="dataDir"/> folder, creating it (random, persisted)
    /// on the first call or if the file is missing/unreadable. Idempotent: later calls return the same value.
    /// </summary>
    public static string GetOrCreate(string dataDir)
    {
        string path = Path.Combine(dataDir, FileName);
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing))
            return existing.ToString();

        Directory.CreateDirectory(dataDir);
        string id = Guid.NewGuid().ToString();
        File.WriteAllText(path, id);
        return id;
    }
}
