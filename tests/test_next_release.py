import unittest

from scripts.next_release import select_version


class ReleaseVersionTests(unittest.TestCase):
    def test_advances_latest_reserved_version(self):
        catalog = [{"guid": "bd36ab75-0f4a-49b6-92ef-3a93da040c7a", "versions": [
            {"version": "0.1.8.7", "checksum": "0" * 32, "sourceUrl": "https://example.test/plugin.zip"}]}]
        self.assertEqual(select_version(catalog, [("v0.1.8.7", "old"), ("v0.1.8.9", "other")], "head"), "0.1.8.10")
        self.assertEqual(select_version(catalog, [("v0.1.8.8", "head")], "head"), "0.1.8.8")
        self.assertEqual(select_version(catalog, [("v0.1.8.7", "head")], "head"), "0.1.8.7")


if __name__ == "__main__":
    unittest.main()
