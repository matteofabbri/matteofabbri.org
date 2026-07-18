using Maddalena.Licensing;

namespace Maddalena.Licensing.Tests;

public class NodeIdentityTests
{
    [Fact]
    public void GetOrCreate_creates_a_stable_guid_per_directory()
    {
        // Throwaway folder next to the test binaries (on the repo drive; never %TEMP%/C:).
        string dir = Path.Combine(AppContext.BaseDirectory, "nodeid-" + Guid.NewGuid().ToString("N")[..8]);
        string otherDir = dir + "-other";
        try
        {
            string first = NodeIdentity.GetOrCreate(dir);
            Assert.True(Guid.TryParse(first, out _));

            // same folder -> same id (persisted)
            Assert.Equal(first, NodeIdentity.GetOrCreate(dir));

            // different folder -> different id
            Assert.NotEqual(first, NodeIdentity.GetOrCreate(otherDir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (Directory.Exists(otherDir)) Directory.Delete(otherDir, recursive: true);
        }
    }

    [Fact]
    public void GetOrCreate_recreates_the_id_when_the_file_is_corrupt()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "nodeid-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "node-id"), "not-a-guid");

            string id = NodeIdentity.GetOrCreate(dir);
            Assert.True(Guid.TryParse(id, out _)); // regenerated
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
