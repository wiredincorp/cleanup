using Cleanup.Interop;
using System.Text.Json.Nodes;
namespace Cleanup.Services;

internal static class OrganizerSmoke
{
    public static async Task<JsonObject> RunAsync(string fixtureDirectory)
    {
        string root = Path.Combine(Path.GetFullPath(fixtureDirectory), "organizer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "fixture.txt");
        await File.WriteAllTextAsync(file, "cleanup organizer smoke fixture");
        OrganizePlan plan = await Features.PlanAsync(root, "type", "none");
        if (plan.Operations.Length != 1) throw new IOException("Organizer preview did not find exactly the disposable fixture.");
        OrganizeApplyResult applied = await Features.ApplyAsync(plan, plan.Operations, "Release smoke fixture");
        if (applied.Moved != 1 || File.Exists(file)) throw new IOException("Production organizer did not move the fixture.");
        UndoResult undone = await Features.UndoAsync(applied.BatchId);
        if (undone.Restored != 1 || await File.ReadAllTextAsync(file) != "cleanup organizer smoke fixture") throw new IOException("Production organizer did not restore the fixture.");
        // Delete only the unique directory this invocation created, after restoration.
        Directory.Delete(root, recursive: true);
        return new JsonObject { ["moved"] = applied.Moved, ["restored"] = undone.Restored, ["batchId"] = applied.BatchId };
    }

    /// <summary>Sorts a disposable photo by its date taken while a text file stays put, then undoes the move.</summary>
    public static async Task<JsonObject> RunPhotosAsync(string fixtureDirectory)
    {
        string root = Path.Combine(Path.GetFullPath(fixtureDirectory), "photos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string photo = Path.Combine(root, "fixture.jpg");
        string other = Path.Combine(root, "fixture.txt");
        byte[] content = JpegTakenOn("2021:06:15 10:00:00");
        await File.WriteAllBytesAsync(photo, content);
        await File.WriteAllTextAsync(other, "cleanup photo smoke fixture");
        OrganizePlan plan = await Features.PlanAsync(root, "date", "yearMonth", media: "photos");
        if (plan.Operations.Length != 1 || plan.OtherFiles != 1 || plan.Operations[0].Captured != "2021-06-15" || plan.Operations[0].DestinationFolder != @"2021\06 - June")
            throw new IOException("Photo preview did not sort the disposable fixture by its date taken.");
        OrganizeApplyResult applied = await Features.ApplyAsync(plan, plan.Operations, "Release smoke photo fixture");
        if (applied.Moved != 1 || File.Exists(photo) || !File.Exists(Path.Combine(root, "2021", "06 - June", "fixture.jpg")) || !File.Exists(other))
            throw new IOException("Photo sorting did not move only the fixture photo.");
        UndoResult undone = await Features.UndoAsync(applied.BatchId);
        if (undone.Restored != 1 || !(await File.ReadAllBytesAsync(photo)).AsSpan().SequenceEqual(content) || Directory.Exists(Path.Combine(root, "2021")))
            throw new IOException("Photo sorting did not restore the fixture.");
        Directory.Delete(root, recursive: true);
        return new JsonObject { ["moved"] = applied.Moved, ["restored"] = undone.Restored, ["captured"] = plan.Operations[0].Captured };
    }

    /// <summary>A JPEG holding only an EXIF DateTimeOriginal. Sorting never reads image data.</summary>
    private static byte[] JpegTakenOn(string taken)
    {
        using var tiff = new MemoryStream();
        using (var writer = new BinaryWriter(tiff))
        {
            writer.Write("II*\0"u8);
            writer.Write(8u);
            // IFD0 points at the EXIF IFD (offset 26), which points at the date (offset 44).
            foreach ((ushort tag, ushort type, uint count, uint value) in new[] { ((ushort)0x8769, (ushort)4, 1u, 26u), ((ushort)0x9003, (ushort)2, 20u, 44u) })
            {
                writer.Write((ushort)1);
                writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value);
                writer.Write(0u);
            }
            writer.Write(System.Text.Encoding.ASCII.GetBytes(taken + "\0"));
        }
        byte[] exif = tiff.ToArray();
        int length = 2 + 6 + exif.Length;
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. "Exif\0\0"u8, .. exif, 0xFF, 0xD9];
    }
}
