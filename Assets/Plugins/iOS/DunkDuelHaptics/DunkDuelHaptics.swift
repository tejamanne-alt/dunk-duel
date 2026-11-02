// Dunk Duel haptics and thermal bridge for iOS (Core Haptics).
// Exposed to Unity C# through @_cdecl C symbols called with DllImport("__Internal").
// Kinds mirror DunkDuel.Presentation.HapticKind: 0 tick, 1 light, 2 medium, 3 heavy, 4 success, 5 warning.

import Foundation
import CoreHaptics
import UIKit

private final class DDHapticsEngine {
    static let shared = DDHapticsEngine()

    private var engine: CHHapticEngine?
    private let supportsHaptics: Bool
    private let fallbackLight = UIImpactFeedbackGenerator(style: .light)
    private let fallbackHeavy = UIImpactFeedbackGenerator(style: .heavy)
    private let fallbackNotify = UINotificationFeedbackGenerator()

    private init() {
        supportsHaptics = CHHapticEngine.capabilitiesForHardware().supportsHaptics
    }

    func prepare() {
        guard supportsHaptics, engine == nil else { return }
        do {
            let e = try CHHapticEngine()
            e.isAutoShutdownEnabled = true
            e.playsHapticsOnly = true
            e.resetHandler = { [weak self] in
                try? self?.engine?.start()
            }
            e.stoppedHandler = { _ in }
            try e.start()
            engine = e
        } catch {
            engine = nil
        }
        fallbackLight.prepare()
        fallbackHeavy.prepare()
    }

    func play(kind: Int32, intensity: Float) {
        let i = max(0, min(1, intensity))
        guard supportsHaptics, let engine = engine else {
            playFallback(kind: kind, intensity: i)
            return
        }
        var events: [CHHapticEvent] = []
        func transient(_ t: TimeInterval, _ amp: Float, _ sharp: Float) {
            events.append(CHHapticEvent(eventType: .hapticTransient,
                                        parameters: [CHHapticEventParameter(parameterID: .hapticIntensity, value: amp * i),
                                                     CHHapticEventParameter(parameterID: .hapticSharpness, value: sharp)],
                                        relativeTime: t))
        }
        switch kind {
        case 0: transient(0, 0.3, 0.8)
        case 1: transient(0, 0.5, 0.6)
        case 2: transient(0, 0.75, 0.5); transient(0.06, 0.4, 0.3)
        case 3:
            transient(0, 1.0, 0.3)
            events.append(CHHapticEvent(eventType: .hapticContinuous,
                                        parameters: [CHHapticEventParameter(parameterID: .hapticIntensity, value: 0.6 * i),
                                                     CHHapticEventParameter(parameterID: .hapticSharpness, value: 0.1)],
                                        relativeTime: 0.02, duration: 0.18))
        case 4: transient(0, 0.6, 0.7); transient(0.09, 0.9, 0.9)
        default: transient(0, 0.5, 1.0); transient(0.12, 0.5, 1.0)
        }
        do {
            let pattern = try CHHapticPattern(events: events, parameters: [])
            let player = try engine.makePlayer(with: pattern)
            try player.start(atTime: CHHapticTimeImmediate)
        } catch {
            playFallback(kind: kind, intensity: i)
        }
    }

    private func playFallback(kind: Int32, intensity: Float) {
        DispatchQueue.main.async {
            switch kind {
            case 3: self.fallbackHeavy.impactOccurred(intensity: CGFloat(intensity))
            case 4: self.fallbackNotify.notificationOccurred(.success)
            case 5: self.fallbackNotify.notificationOccurred(.warning)
            default: self.fallbackLight.impactOccurred(intensity: CGFloat(intensity))
            }
        }
    }
}

@_cdecl("DDHaptics_Prepare")
public func DDHaptics_Prepare() {
    DDHapticsEngine.shared.prepare()
}

@_cdecl("DDHaptics_Play")
public func DDHaptics_Play(_ kind: Int32, _ intensity: Float) {
    DDHapticsEngine.shared.play(kind: kind, intensity: intensity)
}

/// 0 nominal, 1 fair, 2 serious, 3 critical.
@_cdecl("DDHaptics_ThermalState")
public func DDHaptics_ThermalState() -> Int32 {
    switch ProcessInfo.processInfo.thermalState {
    case .nominal: return 0
    case .fair: return 1
    case .serious: return 2
    case .critical: return 3
    @unknown default: return -1
    }
}
