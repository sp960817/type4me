import CoreGraphics

struct RecordingEffectLayout {

    enum Content: Equatable {
        case hidden
        case qwenPlaceholder
        case transcript
    }

    static let storageKey = "tf_showRecordingEffectText"
    static let defaultShowsText = false
    static let defaultTextPaddingWidth: CGFloat = 66.0
    static let defaultCompactWidthRatio: CGFloat = 0.5

    let compactWidth: CGFloat
    let maxWidth: CGFloat
    let textPaddingWidth: CGFloat

    static func defaultCompactWidth(from baseCompactWidth: CGFloat) -> CGFloat {
        baseCompactWidth * defaultCompactWidthRatio
    }

    func recordingWidth(content: Content, peakWidth: CGFloat) -> CGFloat {
        guard content != .hidden else { return compactWidth }
        return peakWidth
    }

    func neededWidth(textWidth: CGFloat) -> CGFloat {
        min(maxWidth, max(compactWidth, textWidth + textPaddingWidth))
    }

    func nextPeakWidth(content: Content, currentPeak: CGFloat, textWidth: CGFloat) -> CGFloat {
        guard content != .hidden else { return compactWidth }

        let needed = neededWidth(textWidth: textWidth)
        if needed > currentPeak || currentPeak - needed > 30 {
            return needed
        }
        return currentPeak
    }

    static func content(showText: Bool, hasSegments: Bool, isQwen3OnlyMode: Bool) -> Content {
        guard showText else { return .hidden }
        if hasSegments { return .transcript }
        return isQwen3OnlyMode ? .qwenPlaceholder : .hidden
    }
}
