using ProjectTabletop.App;

namespace ProjectTabletop.Tests;

public sealed class BoardImageDirectoriesTests
{
    [Theory]
    [InlineData("Paint")]
    [InlineData("Photo Copy")]
    public void ArtworkIsSeparatedByBoardDirectlyUnderLocalPictures(string boardName)
    {
        string actual = boardName == "Paint" ? BoardImageDirectories.Paint : BoardImageDirectories.PhotoCopy;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(boardName, Path.GetFileName(actual));
        Assert.Equal("Pictures", Path.GetFileName(Path.GetDirectoryName(actual)));
        Assert.Equal(profile, Path.GetDirectoryName(Path.GetDirectoryName(actual)));
        Assert.NotEqual(BoardImageDirectories.Paint, BoardImageDirectories.PhotoCopy);
    }
}
