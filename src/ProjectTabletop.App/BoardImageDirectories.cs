namespace ProjectTabletop.App;

// User artwork belongs directly under local Pictures, separated by board.
// Do not follow a redirected Pictures library or move earlier exports.
internal static class BoardImageDirectories
{
    public static string Paint => ForBoard("Paint");
    public static string Globe => ForBoard("Globe");
    public static string PhotoCopy => ForBoard("Photo Copy");

    private static string ForBoard(string boardName)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            throw new DirectoryNotFoundException("Windows did not provide a user profile folder.");
        return Path.Combine(profile, "Pictures", boardName);
    }
}
