import XCTest
@testable import Type4Me

final class RecordingEffectLayoutTests: XCTestCase {

    private let layout = RecordingEffectLayout(
        compactWidth: 100,
        maxWidth: 400,
        textPaddingWidth: 66
    )

    func testDefaultHidesRecordingEffectText() {
        XCTAssertEqual(RecordingEffectLayout.storageKey, "tf_showRecordingEffectText")
        XCTAssertFalse(RecordingEffectLayout.defaultShowsText)
    }

    func testDefaultCompactWidthIsHalfOfBaseCompactWidth() {
        XCTAssertEqual(RecordingEffectLayout.defaultCompactWidth(from: 200), 100)
    }

    func testContentIsHiddenWhenToggleIsOff() {
        XCTAssertEqual(
            RecordingEffectLayout.content(showText: false, hasSegments: true, isQwen3OnlyMode: true),
            .hidden
        )
        XCTAssertEqual(
            RecordingEffectLayout.content(showText: false, hasSegments: false, isQwen3OnlyMode: true),
            .hidden
        )
    }

    func testContentUsesPlaceholderForQwen3OnlyRecordingWhenToggleIsOn() {
        XCTAssertEqual(
            RecordingEffectLayout.content(showText: true, hasSegments: false, isQwen3OnlyMode: true),
            .qwenPlaceholder
        )
    }

    func testContentShowsTranscriptOnlyWhenToggleIsOnAndSegmentsExist() {
        XCTAssertEqual(
            RecordingEffectLayout.content(showText: true, hasSegments: true, isQwen3OnlyMode: false),
            .transcript
        )
        XCTAssertEqual(
            RecordingEffectLayout.content(showText: true, hasSegments: false, isQwen3OnlyMode: false),
            .hidden
        )
    }

    func testRecordingWidthStaysCompactWhenToggleIsOff() {
        XCTAssertEqual(
            layout.recordingWidth(content: .hidden, peakWidth: 390),
            100
        )
    }

    func testRecordingWidthUsesPeakWhenTextIsVisible() {
        XCTAssertEqual(
            layout.recordingWidth(content: .qwenPlaceholder, peakWidth: 130),
            130
        )
        XCTAssertEqual(
            layout.recordingWidth(content: .transcript, peakWidth: 390),
            390
        )
    }

    func testNeededWidthClampsToCompactAndMaxWidth() {
        XCTAssertEqual(layout.neededWidth(textWidth: 20), 100)
        XCTAssertEqual(layout.neededWidth(textWidth: 200), 266)
        XCTAssertEqual(layout.neededWidth(textWidth: 500), 400)
    }

    func testNextPeakWidthResetsToCompactWhenToggleIsOff() {
        XCTAssertEqual(
            layout.nextPeakWidth(content: .hidden, currentPeak: 390, textWidth: 500),
            100
        )
    }

    func testNextPeakWidthGrowsForLongerText() {
        XCTAssertEqual(
            layout.nextPeakWidth(content: .transcript, currentPeak: 220, textWidth: 260),
            326
        )
    }

    func testNextPeakWidthGrowsForQwenPlaceholderText() {
        XCTAssertEqual(
            layout.nextPeakWidth(content: .qwenPlaceholder, currentPeak: 100, textWidth: 70),
            136
        )
    }

    func testNextPeakWidthKeepsPeakForSmallCorrections() {
        XCTAssertEqual(
            layout.nextPeakWidth(content: .transcript, currentPeak: 350, textWidth: 260),
            350
        )
    }

    func testNextPeakWidthShrinksForLargeCorrections() {
        XCTAssertEqual(
            layout.nextPeakWidth(content: .transcript, currentPeak: 390, textWidth: 260),
            326
        )
    }
}
