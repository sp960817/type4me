import XCTest
@testable import Type4Me

final class UpdateInfoTests: XCTestCase {

    func testDownloadURLUsesLocalAssetForLocalInstallations() throws {
        let update = try decodeUpdate("""
        {
          "version": "1.9.5",
          "date": "2026-06-01",
          "notes": "Fix updater",
          "cloud_dmg_url": "https://example.com/Type4Me-cloud.dmg",
          "cloud_dmg_size": 1234,
          "cloud_dmg_sha256": "cloudhash",
          "local_dmg_url": "https://example.com/Type4Me-local.dmg",
          "local_dmg_size": 5678,
          "local_dmg_sha256": "localhash"
        }
        """)

        XCTAssertEqual(
            update.downloadURL(isLocalInstallation: false).absoluteString,
            "https://example.com/Type4Me-cloud.dmg"
        )
        XCTAssertEqual(
            update.downloadURL(isLocalInstallation: true).absoluteString,
            "https://example.com/Type4Me-local.dmg"
        )
        XCTAssertEqual(update.dmgSHA256(isLocalInstallation: false), "cloudhash")
        XCTAssertEqual(update.dmgSHA256(isLocalInstallation: true), "localhash")
        XCTAssertNotNil(update.formattedSize(isLocalInstallation: false))
        XCTAssertNotNil(update.formattedSize(isLocalInstallation: true))
    }

    func testDownloadURLFallsBackToVariantSpecificReleaseAssetNames() throws {
        let update = try decodeUpdate("""
        {
          "version": "1.9.5",
          "date": "2026-06-01",
          "notes": "Fix updater"
        }
        """)

        XCTAssertEqual(
            update.downloadURL(isLocalInstallation: false).absoluteString,
            "https://github.com/joewongjc/type4me/releases/download/v1.9.5/Type4Me-v1.9.5-cloud.dmg"
        )
        XCTAssertEqual(
            update.downloadURL(isLocalInstallation: true).absoluteString,
            "https://github.com/joewongjc/type4me/releases/download/v1.9.5/Type4Me-v1.9.5-local-apple-silicon.dmg"
        )
    }

    private func decodeUpdate(_ json: String) throws -> UpdateInfo {
        try JSONDecoder().decode(UpdateInfo.self, from: Data(json.utf8))
    }
}
