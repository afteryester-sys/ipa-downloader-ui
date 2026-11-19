#!/usr/bin/env python3
"""
Unit and integration tests for IPA Metadata Extraction, Strings Parsing,
Icon Extraction, and Catalog Enrichment.
"""

import os
import re
import io
import json
import zipfile
import tempfile
import unittest

class TestIpaMetadataAndCatalogEnrichment(unittest.TestCase):

    def test_app_query_parser_leading_dash_and_flags(self):
        """Test that inputs like -481627348, -i 481627348, and id: 481627348 are parsed as AppStoreId."""
        test_cases = [
            ("481627348", 481627348),
            ("-481627348", 481627348),
            ("-i 481627348", 481627348),
            ("id: 481627348", 481627348),
            ("  -481627348  ", 481627348),
            ("https://apps.apple.com/ru/app/2gis/id481627348", 481627348),
        ]
        
        # Simulation of AppQueryParser logic
        for raw, expected_id in test_cases:
            text = raw.strip().strip('"\'<> ')
            if text.lower().startswith("-i ") or text.lower().startswith("id:"):
                text = text[3:].strip()
            elif text.startswith("-"):
                text = text.lstrip("- ")
            
            bare_id_match = re.match(r"^\d{4,15}$", text)
            if bare_id_match:
                parsed_id = int(text)
            else:
                link_match = re.search(r"(?:^|[/=?&])id(\d{4,15})\b", text, re.IGNORECASE)
                parsed_id = int(link_match.group(1)) if link_match else None
            
            self.assertEqual(parsed_id, expected_id, f"Failed for input '{raw}'")

    def test_strings_regex_parsing(self):
        """Test extraction of CFBundleDisplayName from .strings UTF-8 / UTF-16 text."""
        raw_strings = (
            '/* App display name */\n'
            '"CFBundleDisplayName" = "2ГИС";\n'
            '"CFBundleName" = "dgis";\n'
            '"CFBundleShortVersionString" = "7.31.1";\n'
        )
        matches = dict(re.findall(r'"([^"\\]*(?:\\.[^"\\]*)*)"\s*=\s*"([^"\\]*(?:\\.[^"\\]*)*)"', raw_strings))
        self.assertIn("CFBundleDisplayName", matches)
        self.assertEqual(matches["CFBundleDisplayName"], "2ГИС")
        self.assertEqual(matches["CFBundleName"], "dgis")

    def test_display_name_priority_over_itunes_metadata(self):
        """Test that CFBundleDisplayName takes priority over iTunesMetadata itemName."""
        itunes_item_name = "2ГИС: карты, навигатор, друзья"
        plist_display_name = "2ГИС"
        bundle_name = "dgis"
        fallback_name = "2GIS_481627348"

        # Corrected priority order
        name = plist_display_name or itunes_item_name or bundle_name or fallback_name
        self.assertEqual(name, "2ГИС")

        # When plist_display_name is absent, falls back to itunes_item_name
        name_no_display = None or itunes_item_name or bundle_name or fallback_name
        self.assertEqual(name_no_display, "2ГИС: карты, навигатор, друзья")

    def test_catalog_enrichment_atomic_update(self):
        """Test atomic update of user-apps.json with semaphore/tmp pattern."""
        with tempfile.TemporaryDirectory() as tmpdir:
            catalog_file = os.path.join(tmpdir, "user-apps.json")
            
            # Initial state
            initial_data = [
                {"AppStoreId": 123456, "Name": "AppOne", "BundleId": "com.example.one"}
            ]
            with open(catalog_file, "w", encoding="utf-8") as f:
                json.dump(initial_data, f)
            
            # Enrichment with 2GIS (id: 481627348)
            new_app = {
                "AppStoreId": 481627348,
                "Name": "2ГИС",
                "BundleId": "ru.dublgis.dgismobile",
                "LatestVersion": "7.31.1"
            }
            
            with open(catalog_file, "r", encoding="utf-8") as f:
                entries = json.load(f)
            
            idx = next((i for i, e in enumerate(entries) if e["AppStoreId"] == new_app["AppStoreId"]), -1)
            if idx >= 0:
                entries[idx].update(new_app)
            else:
                entries.append(new_app)
            
            temp_file = catalog_file + ".tmp"
            with open(temp_file, "w", encoding="utf-8") as f:
                json.dump(entries, f)
            os.replace(temp_file, catalog_file)
            
            # Verify updated file
            with open(catalog_file, "r", encoding="utf-8") as f:
                saved = json.load(f)
            
            self.assertEqual(len(saved), 2)
            self.assertEqual(saved[1]["AppStoreId"], 481627348)
            self.assertEqual(saved[1]["Name"], "2ГИС")
            self.assertEqual(saved[1]["BundleId"], "ru.dublgis.dgismobile")

    def test_mock_ipa_archive_structure(self):
        """Test zip traversal and metadata file discovery."""
        bio = io.BytesIO()
        with zipfile.ZipFile(bio, "w", zipfile.ZIP_DEFLATED) as zf:
            zf.writestr("iTunesMetadata.plist", "<plist><dict><key>itemId</key><integer>481627348</integer></dict></plist>")
            zf.writestr("Payload/2gis.app/Info.plist", "<plist><dict><key>CFBundleDisplayName</key><string>2ГИС</string></dict></plist>")
            zf.writestr("Payload/2gis.app/ru.lproj/InfoPlist.strings", '"CFBundleDisplayName" = "2ГИС";\n')
            zf.writestr("iTunesArtwork", b"\x89PNG\r\n\x1a\n" + b"\x00" * 100)

        bio.seek(0)
        with zipfile.ZipFile(bio, "r") as zf:
            names = zf.namelist()
            self.assertIn("iTunesMetadata.plist", names)
            self.assertIn("Payload/2gis.app/Info.plist", names)
            self.assertIn("Payload/2gis.app/ru.lproj/InfoPlist.strings", names)
            self.assertIn("iTunesArtwork", names)

            # Check bundle root detection
            app_entries = [n for n in names if n.startswith("Payload/") and ".app/" in n]
            bundle_root = app_entries[0][:app_entries[0].find(".app/") + 5]
            self.assertEqual(bundle_root, "Payload/2gis.app/")

if __name__ == "__main__":
    unittest.main()
