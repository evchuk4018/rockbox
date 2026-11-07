import unittest

import tag_repair as tag


class GenreTests(unittest.TestCase):
    def test_tokens_keep_slash_genres(self):
        self.assertEqual(tag.genre_tokens("r&b; alternative r&b"), ["r&b", "alternative r&b"])
        self.assertEqual(tag.genre_tokens("Hip-Hop/Rap"), ["hip-hop/rap"])

    def test_term_mapping(self):
        self.assertEqual(tag.map_term("dance-pop"), ("Pop", "Pop-Dance"))
        self.assertEqual(tag.map_term("trap"), ("Hip-Hop/Rap", "Hip-Hop/Rap-Trap"))
        self.assertEqual(tag.map_term("soul"), ("Pop", "Pop-Soul"))
        self.assertEqual(tag.map_term("conscious hip hop"), ("Hip-Hop/Rap", "Hip-Hop/Rap-Conscious"))

    def test_taylor_album_rule(self):
        self.assertEqual(tag.classify_genre("Taylor Swift", "Blank Space", "1989", ["Pop"], [], {}), "Pop-Dance")
        self.assertEqual(tag.classify_genre("Taylor Swift", "Love Story", "Fearless Platinum Edition", ["Country pop"], [], {}), "Country-Pop")

    def test_buttercup_is_soul(self):
        self.assertEqual(tag.classify_genre("The Foundations", "Build Me Up Buttercup (Mono)", "From the Foundations", ["Pop"], ["soul"], {}), "Pop-Soul")

    def test_tv_girl_override(self):
        self.assertEqual(tag.classify_genre("TV Girl", "Lovers Rock", "Lovers Rock", ["electronic; hip hop"], [], {}), "Pop-Indie")

    def test_profile_used_for_bare_pop(self):
        profile = tag.profile_from_terms(["dance-pop", "electropop", "pop"])
        self.assertEqual(tag.classify_genre("Katy Perry", "Firework", "Teenage Dream", ["Pop"], profile, {}), "Pop-Dance")

    def test_every_result_is_allowed(self):
        for genre in tag.ALLOWED_GENRES:
            self.assertTrue(tag.is_allowed(genre))
        self.assertFalse(tag.is_allowed("Pop"))
        self.assertFalse(tag.is_allowed("pop; pop rock"))


class TextTests(unittest.TestCase):
    def test_title_prefix(self):
        self.assertEqual(tag.clean_title("Taylor Swift - Cruel Summer", "Taylor Swift"), "Cruel Summer")
        self.assertEqual(tag.clean_title("Katy Perry - Last Friday Night (T.G.I.F) (Lyrics)", "Katy Perry"), "Last Friday Night (T.G.I.F)")
        self.assertEqual(tag.clean_title("Katy Perry - The One That Got Away (Official Music Video)", "Katy Perry"), "The One That Got Away")

    def test_title_versions_kept(self):
        self.assertEqual(tag.clean_title("Blow (Kryd Hoodtrap / Mylancore Remix)", "Kesha"), "Blow (Kryd Hoodtrap / Mylancore Remix)")
        self.assertEqual(tag.clean_title("My Eyes (Slowed + Reverb)", "Travis Scott"), "My Eyes (Slowed + Reverb)")

    def test_albumartist_aliases(self):
        self.assertEqual(tag.canonical_albumartist("ケイティ・ペリー", "Katy Perry", "Teenage Dream"), "Katy Perry")
        self.assertEqual(tag.canonical_albumartist("奧莉維亞", "Olivia Rodrigo", "SOUR"), "Olivia Rodrigo")
        self.assertEqual(tag.canonical_albumartist("ビリー・アイリッシュ", "Billie Eilish", "Happier Than Ever"), "Billie Eilish")

    def test_album_placeholders_removed(self):
        self.assertIsNone(tag.canonical_album("Study (One-Time)", "Sidney Gish", "Impostor Syndrome"))
        self.assertIsNone(tag.canonical_album("Apr 21, 2025", "Kesha", "Blow"))


class DuplicateTests(unittest.TestCase):
    def test_recording_key_tolerates_codec_duration_rounding(self):
        a = {"artist": "Taylor Swift", "title": "Blank Space", "duration": 231.82}
        b = {"artist": "taylor swift", "title": "blank space", "duration": 232.4}
        self.assertEqual(tag.recording_key(a), tag.recording_key(b))

    def test_choose_keeper_prefers_quality_then_size(self):
        low = {"path": "low.mp3", "bitrate": 128000, "size": 100, "date": "2014", "duration": 200.0}
        high = {"path": "high.mp3", "bitrate": 320000, "size": 90, "date": "", "duration": 200.1}
        self.assertEqual(tag.choose_keeper([low, high])["path"], "high.mp3")

    def test_same_title_different_duration_is_not_duplicate(self):
        self.assertNotEqual(
            tag.recording_key({"artist": "A", "title": "T", "duration": 200}),
            tag.recording_key({"artist": "A", "title": "T", "duration": 240}),
        )


class PoolTests(unittest.TestCase):
    def test_small_pools_merge(self):
        planned = [
            {"genre": "Pop-Dance"}, {"genre": "Pop-Dance"}, {"genre": "Pop-Dance"},
            {"genre": "Pop-Dance"}, {"genre": "Pop-Dance"},
            {"genre": "Pop-Latin"}, {"genre": "Pop-Latin"},
        ]
        merged = tag.merge_small_pools(planned, minimum=5)
        self.assertEqual(merged["Pop-Latin"], "Pop-Dance")


if __name__ == "__main__":
    unittest.main()
