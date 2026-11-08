using RockboxManager.Core;

static class ThumbnailCropTests
{
    private static RgbaImage Image(int width, int height, Func<int, int, byte> value)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int p = (y * width + x) * 4; pixels[p] = pixels[p + 1] = pixels[p + 2] = value(x, y); pixels[p + 3] = 255;
        }
        return new(width, height, pixels);
    }

    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        test("Thumbnail crop preserves square images and trims paired black bands", () =>
        {
            var square = Image(40, 40, (x, y) => (byte)(x * 6 + y % 6));
            check(CoverConverter.Convert(square).SequenceEqual(CoverConverter.Convert(square, thumbnail: true)));
            var wide = Image(80, 60, (x, y) => y < 10 || y >= 50 ? (byte)0 : (byte)(40 + x * 2));
            var automatic = CoverConverter.Convert(wide, thumbnail: true);
            check(automatic.SequenceEqual(CoverConverter.Convert(wide, crop: new(20, 10, 40))));
            check(!automatic.SequenceEqual(CoverConverter.Convert(wide)) && CoverConverter.IsCover(automatic) && automatic.Length == 550);
            var dark = Image(80, 60, (x, y) => (byte)(x % 12));
            check(CoverConverter.Convert(dark).SequenceEqual(CoverConverter.Convert(dark, thumbnail: true)));
        });
        test("Explicit crop validates bounds and preserves legacy inset behavior", () =>
        {
            var image = Image(80, 60, (x, y) => (byte)(x < 40 ? (x % 8) * 30 : (y % 8) * 30));
            check(CoverConverter.Convert(image, thumbnail: true, crop: new(0, 0, 30)).SequenceEqual(CoverConverter.Convert(image, crop: new(0, 0, 30))));
            check(!CoverConverter.Convert(image, crop: new(0, 0, 30)).SequenceEqual(CoverConverter.Convert(image, crop: new(50, 0, 30))));
            check(CoverConverter.Convert(image, 2).SequenceEqual(CoverConverter.Convert(image, 2, thumbnail: true)));
            foreach (var crop in new[] { new CropRectangle(-1, 0, 10), new(0, 0, 0), new(70, 0, 11), new(0, 59, 2) })
            {
                bool failed = false; try { CoverConverter.Convert(image, crop: crop); } catch (ArgumentException) { failed = true; }
                check(failed);
            }
        });
        test("Generic covers require the original song alone in the same folder and volume at send time", () =>
        {
            using var fixture = new Fixture(); fixture.Write(Storage.Sd, "Solo/one.mp3", "audio");
            var song = new SongPath(Storage.Sd, "Solo/one.mp3");
            var change = new FileChange { Storage = Storage.Sd, RelativePath = "Solo/cover.61x61.bmp", ArtworkTrack = song,
                Content = CoverConverter.Convert(Image(10, 10, (_, _) => 255)) };
            check(SyncEngine.SafePath(fixture.Device, change).EndsWith("cover.61x61.bmp"));
            foreach (var forged in new[] { new FileChange { Storage = Storage.Internal, RelativePath = change.RelativePath, ArtworkTrack = song, Content = change.Content },
                new FileChange { Storage = Storage.Sd, RelativePath = "M/cover.61x61.bmp", ArtworkTrack = song, Content = change.Content } })
            {
                bool rejected = false; try { SyncEngine.SafePath(fixture.Device, forged); } catch (InvalidOperationException) { rejected = true; }
                check(rejected);
            }
            var session = EditSession.Load(fixture.Device); session.ArtworkChanges.Add(change);
            var batch = SyncBatch.Build(session, fixture.Backups);
            fixture.Write(Storage.Sd, "Solo/two.flac", "audio");
            check(!new SyncEngine().Execute(batch, () => fixture.Device).Success && !File.Exists(fixture.PathFor(Storage.Sd, change.RelativePath)));
        });
    }
}
