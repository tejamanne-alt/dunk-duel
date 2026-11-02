// DunkDuelBLE.swift
// CoreBluetooth bridge for Dunk Duel on iOS. C ABI consumed by
// Assets/DunkDuel/Transport.BLE/IosBleBackend.cs via DllImport("__Internal").
//
// Threading: every CoreBluetooth delegate callback runs on `queue` (serial). The exported functions are
// called from the Unity main thread and hop onto `queue`. Records for C# sit in `records`, guarded by
// `lock`, and are drained once per frame by dd_ble_poll. L2CAP streams (experimental, off by default) are
// scheduled on the main run loop and their state is main-thread confined.
//
// Wire constants mirror Assets/DunkDuel/Transport.BLE/BleProtocol.cs.

import CoreBluetooth
import Foundation

private enum Rec {
    static let input: UInt8 = 0
    static let control: UInt8 = 1
    static let state: UInt8 = 2
    static let peer: UInt8 = 3
    static let link: UInt8 = 4
    static let canHost: UInt8 = 5
    static let dropped: UInt8 = 6
}

// Numeric values of DunkDuel.Net.TransportState / TransportError.
private enum LinkState: UInt8 {
    case idle = 0, hosting = 1, scanning = 2, connecting = 3, connected = 4, disconnected = 5, failed = 6
}

private enum LinkError: UInt8 {
    case none = 0, radioOff = 1, permissionDenied = 2, advertisingUnsupported = 3, connectFailed = 4,
         protocolMismatch = 5, internalError = 6
}

final class DunkDuelBLE: NSObject {
    static let shared = DunkDuelBLE()

    static let serviceUUID = CBUUID(string: "DD0E0001-8E2A-4C1B-9D3F-6A5B4C3D2E1F")
    static let inputUUID = CBUUID(string: "DD0E0002-8E2A-4C1B-9D3F-6A5B4C3D2E1F")
    static let controlUUID = CBUUID(string: "DD0E0003-8E2A-4C1B-9D3F-6A5B4C3D2E1F")
    static let psmUUID = CBUUID(string: "DD0E0004-8E2A-4C1B-9D3F-6A5B4C3D2E1F")

    private let maxInputQueue = 4
    private let maxPendingRecords = 4096
    private let connectTimeout: TimeInterval = 10
    private let peerReportInterval: TimeInterval = 0.25

    // Records waiting for C#.
    private let lock = NSLock()
    private var records: [Data] = []

    // --- queue-confined state ---
    private let queue = DispatchQueue(label: "com.dunkduel.ble", qos: .userInteractive)
    private enum Role { case none, host, guest }
    private var role: Role = .none
    private var useL2cap = false
    private var connected = false
    private var everConnected = false
    private var outInput: [Data] = []
    private var outControl: [Data] = []

    // Host (peripheral)
    private var peripheralManager: CBPeripheralManager?
    private var hostRequested = false
    private var localName = "DD-0000"
    private var inputChar: CBMutableCharacteristic?
    private var controlChar: CBMutableCharacteristic?
    private var psmChar: CBMutableCharacteristic?
    private var subscriber: CBCentral?
    private var inputSubscribed = false
    private var controlSubscribed = false
    private var publishedPSM: CBL2CAPPSM = 0

    // Guest (central)
    private var centralManager: CBCentralManager?
    private var scanRequested = false
    private var discovered: [UUID: CBPeripheral] = [:]
    private var lastPeerReport: [UUID: TimeInterval] = [:]
    private var joinTarget: UUID?
    private var remote: CBPeripheral?
    private var remoteInput: CBCharacteristic?
    private var remoteControl: CBCharacteristic?
    private var remotePSM: CBCharacteristic?
    private var inputNotifying = false
    private var controlNotifying = false
    private var controlInFlight = false
    private var connectAttempt = 0

    // --- main-thread-confined L2CAP state ---
    private var l2capChannel: CBL2CAPChannel?
    private var l2capReady = false
    private var l2capIn = Data()
    private var l2capOut = Data()

    // MARK: - Commands (called from the Unity main thread)

    func initialize(useL2cap: Bool) {
        queue.async {
            self.useL2cap = useL2cap
            self.emit(Rec.canHost, Data([1])) // iOS can always advertise while in the foreground
        }
    }

    func host(name: String) {
        queue.async {
            self.teardownLinks(emitIdle: false)
            self.role = .host
            self.hostRequested = true
            self.everConnected = false
            self.localName = name
            if let pm = self.peripheralManager {
                if pm.state == .poweredOn { self.setupService() }
            } else {
                // Creating the manager shows the Bluetooth permission prompt the first time.
                self.peripheralManager = CBPeripheralManager(
                    delegate: self, queue: self.queue,
                    options: [CBPeripheralManagerOptionShowPowerAlertKey: true])
            }
        }
    }

    func scan(_ enable: Bool) {
        queue.async {
            if enable {
                self.scanRequested = true
                if self.role == .none { self.role = .guest }
                self.discovered.removeAll()
                self.lastPeerReport.removeAll()
                if let cm = self.centralManager {
                    if cm.state == .poweredOn { self.beginScan() }
                } else {
                    self.centralManager = CBCentralManager(
                        delegate: self, queue: self.queue,
                        options: [CBCentralManagerOptionShowPowerAlertKey: true])
                }
            } else {
                self.scanRequested = false
                if let cm = self.centralManager, cm.state == .poweredOn, cm.isScanning { cm.stopScan() }
                if self.role == .guest && self.remote == nil && !self.connected { self.role = .none }
            }
        }
    }

    func join(address: String) {
        queue.async {
            guard let id = UUID(uuidString: address) else {
                self.fail(.connectFailed)
                return
            }
            let known = self.discovered
            self.teardownLinks(emitIdle: false)
            self.discovered = known
            self.role = .guest
            self.joinTarget = id
            self.everConnected = false
            if let cm = self.centralManager {
                if cm.state == .poweredOn { self.connect(id) }
            } else {
                self.centralManager = CBCentralManager(
                    delegate: self, queue: self.queue,
                    options: [CBCentralManagerOptionShowPowerAlertKey: true])
            }
        }
    }

    /// Main thread. Returns 1 when queued. INPUT may be dropped later under back-pressure (reported as Rec.dropped).
    func send(channel: Int32, data: Data) -> Int32 {
        if channel == 0 && l2capReady {
            l2capEnqueue(channel: 0, data: data)
            return 1
        }
        queue.async {
            guard self.connected else { return }
            if channel == 0 {
                self.outInput.append(data)
                let excess = self.outInput.count - self.maxInputQueue
                if excess > 0 {
                    self.outInput.removeFirst(excess) // newer packets carry the last 8 inputs anyway
                    self.emitDropped(excess)
                }
            } else {
                self.outControl.append(data)
            }
            switch self.role {
            case .host: self.flushHost()
            case .guest: self.flushGuest()
            case .none: break
            }
        }
        return 1
    }

    func disconnect() {
        queue.async { self.teardownLinks(emitIdle: true) }
    }

    func shutdown() {
        queue.sync {
            self.teardownLinks(emitIdle: false)
            self.peripheralManager?.delegate = nil
            self.centralManager?.delegate = nil
            self.peripheralManager = nil
            self.centralManager = nil
        }
        lock.lock()
        records.removeAll()
        lock.unlock()
    }

    /// Main thread: copies whole records into the caller's buffer.
    func drain(into buffer: UnsafeMutablePointer<UInt8>, capacity: Int) -> Int32 {
        lock.lock()
        defer { lock.unlock() }
        var written = 0
        var taken = 0
        for r in records {
            if written + r.count > capacity { break }
            r.copyBytes(to: buffer.advanced(by: written), count: r.count)
            written += r.count
            taken += 1
        }
        if taken > 0 { records.removeFirst(taken) }
        return Int32(written)
    }

    // MARK: - Records

    private func emit(_ kind: UInt8, _ payload: Data) {
        var r = Data(capacity: payload.count + 3)
        r.append(kind)
        r.append(UInt8(truncatingIfNeeded: payload.count))
        r.append(UInt8(truncatingIfNeeded: payload.count >> 8))
        r.append(payload)
        lock.lock()
        if records.count < maxPendingRecords { records.append(r) }
        lock.unlock()
    }

    private func emitState(_ s: LinkState, _ e: LinkError = .none) {
        emit(Rec.state, Data([s.rawValue, e.rawValue]))
    }

    private func fail(_ e: LinkError) {
        emitState(.failed, e)
    }

    private func emitLink(maxPayload: Int) {
        let p = UInt16(clamping: maxPayload)
        emit(Rec.link, Data([UInt8(p & 0xFF), UInt8(p >> 8), 0, 0, 0, 0]))
    }

    private func emitDropped(_ count: Int) {
        let v = UInt32(clamping: count)
        emit(Rec.dropped, Data([UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF), UInt8((v >> 16) & 0xFF), UInt8(v >> 24)]))
    }

    private func emitPeer(_ id: UUID, rssi: Int, name: String?, serviceData: Data?) {
        var p = Data()
        p.append(UInt8(bitPattern: Int8(clamping: rssi)))
        let addr = Data(id.uuidString.utf8).prefix(255)
        p.append(UInt8(truncatingIfNeeded: addr.count))
        p.append(addr)
        let n = Data((name ?? "").utf8).prefix(255)
        p.append(UInt8(truncatingIfNeeded: n.count))
        p.append(n)
        let sd = (serviceData ?? Data()).prefix(255)
        p.append(UInt8(truncatingIfNeeded: sd.count))
        p.append(sd)
        emit(Rec.peer, p)
    }

    // MARK: - Teardown

    private func teardownLinks(emitIdle: Bool) {
        if let pm = peripheralManager, pm.state == .poweredOn {
            if pm.isAdvertising { pm.stopAdvertising() }
            pm.removeAllServices()
            if publishedPSM != 0 { pm.unpublishL2CAPChannel(publishedPSM) }
        }
        publishedPSM = 0
        hostRequested = false
        subscriber = nil
        inputSubscribed = false
        controlSubscribed = false
        inputChar = nil
        controlChar = nil
        psmChar = nil

        scanRequested = false
        if let cm = centralManager, cm.state == .poweredOn {
            if cm.isScanning { cm.stopScan() }
            if let r = remote { cm.cancelPeripheralConnection(r) }
        }
        resetGuestLink()
        joinTarget = nil
        connectAttempt &+= 1

        connected = false
        outInput.removeAll()
        outControl.removeAll()
        role = .none
        DispatchQueue.main.async { self.closeL2cap() }
        if emitIdle { emitState(.idle) }
    }

    private func resetGuestLink() {
        remote?.delegate = nil
        remote = nil
        remoteInput = nil
        remoteControl = nil
        remotePSM = nil
        inputNotifying = false
        controlNotifying = false
        controlInFlight = false
    }
}

// MARK: - Host: CBPeripheralManagerDelegate

extension DunkDuelBLE: CBPeripheralManagerDelegate {
    func peripheralManagerDidUpdateState(_ peripheral: CBPeripheralManager) {
        switch peripheral.state {
        case .poweredOn:
            if role == .host && hostRequested { setupService() }
        case .poweredOff:
            if role == .host {
                dropHostLink()
                fail(.radioOff)
            }
        case .unauthorized:
            if role == .host { fail(.permissionDenied) }
        case .unsupported:
            emit(Rec.canHost, Data([0]))
            if role == .host { fail(.advertisingUnsupported) }
        default:
            break
        }
    }

    private func setupService() {
        guard let pm = peripheralManager, pm.state == .poweredOn else { return }
        let input = CBMutableCharacteristic(
            type: DunkDuelBLE.inputUUID, properties: [.writeWithoutResponse, .notify],
            value: nil, permissions: [.writeable])
        let control = CBMutableCharacteristic(
            type: DunkDuelBLE.controlUUID, properties: [.write, .indicate],
            value: nil, permissions: [.writeable])
        let psm = CBMutableCharacteristic(
            type: DunkDuelBLE.psmUUID, properties: [.read], value: nil, permissions: [.readable])
        let service = CBMutableService(type: DunkDuelBLE.serviceUUID, primary: true)
        service.characteristics = [input, control, psm]
        inputChar = input
        controlChar = control
        psmChar = psm
        pm.removeAllServices()
        pm.add(service)
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didAdd service: CBService, error: Error?) {
        guard role == .host else { return }
        if error != nil {
            fail(.internalError)
            return
        }
        if useL2cap && publishedPSM == 0 { peripheral.publishL2CAPChannel(withEncryption: false) }
        startAdvertising()
    }

    private func startAdvertising() {
        guard let pm = peripheralManager, pm.state == .poweredOn, role == .host, hostRequested else { return }
        if pm.isAdvertising { pm.stopAdvertising() }
        pm.startAdvertising([
            CBAdvertisementDataLocalNameKey: localName,
            CBAdvertisementDataServiceUUIDsKey: [DunkDuelBLE.serviceUUID],
        ])
    }

    func peripheralManagerDidStartAdvertising(_ peripheral: CBPeripheralManager, error: Error?) {
        guard role == .host else { return }
        if error != nil {
            fail(.advertisingUnsupported)
            return
        }
        // After a drop the host re-advertises silently so the same guest can reconnect.
        if !everConnected { emitState(.hosting) }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, central: CBCentral, didSubscribeTo characteristic: CBCharacteristic) {
        guard role == .host else { return }
        if let s = subscriber, s.identifier != central.identifier { return } // one guest only
        if subscriber == nil {
            subscriber = central
            peripheral.setDesiredConnectionLatency(.low, for: central)
        }
        if characteristic.uuid == DunkDuelBLE.inputUUID { inputSubscribed = true }
        if characteristic.uuid == DunkDuelBLE.controlUUID { controlSubscribed = true }
        if inputSubscribed && controlSubscribed && !connected {
            connected = true
            everConnected = true
            if peripheral.isAdvertising { peripheral.stopAdvertising() }
            emitLink(maxPayload: central.maximumUpdateValueLength)
            emitState(.connected)
            flushHost()
        }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, central: CBCentral, didUnsubscribeFrom characteristic: CBCharacteristic) {
        guard role == .host, let s = subscriber, s.identifier == central.identifier else { return }
        let was = connected
        dropHostLink()
        if was { emitState(.disconnected) }
        startAdvertising()
    }

    private func dropHostLink() {
        let was = connected
        connected = false
        subscriber = nil
        inputSubscribed = false
        controlSubscribed = false
        outInput.removeAll()
        outControl.removeAll()
        if was { DispatchQueue.main.async { self.closeL2cap() } }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveWrite requests: [CBATTRequest]) {
        var needsResponse = false
        for r in requests {
            if r.characteristic.uuid == DunkDuelBLE.controlUUID { needsResponse = true }
            guard role == .host, connected, let s = subscriber, r.central.identifier == s.identifier,
                  let v = r.value else { continue }
            if r.characteristic.uuid == DunkDuelBLE.inputUUID {
                emit(Rec.input, v)
            } else if r.characteristic.uuid == DunkDuelBLE.controlUUID {
                emit(Rec.control, v)
            }
        }
        // Writes with response (CONTROL) expect exactly one response per batch; INPUT writes expect none.
        if needsResponse, let first = requests.first { peripheral.respond(to: first, withResult: .success) }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveRead request: CBATTRequest) {
        guard request.characteristic.uuid == DunkDuelBLE.psmUUID else {
            peripheral.respond(to: request, withResult: .readNotPermitted)
            return
        }
        let value = Data([UInt8(publishedPSM & 0xFF), UInt8(publishedPSM >> 8)])
        if request.offset > value.count {
            peripheral.respond(to: request, withResult: .invalidOffset)
            return
        }
        request.value = value.subdata(in: request.offset..<value.count)
        peripheral.respond(to: request, withResult: .success)
    }

    func peripheralManagerIsReady(toUpdateSubscribers peripheral: CBPeripheralManager) {
        flushHost()
    }

    private func flushHost() {
        guard connected, let pm = peripheralManager, let s = subscriber,
              let ic = inputChar, let cc = controlChar else { return }
        while !outControl.isEmpty || !outInput.isEmpty {
            if let d = outControl.first {
                if !pm.updateValue(d, for: cc, onSubscribedCentrals: [s]) { return } // wait for isReady
                outControl.removeFirst()
            }
            if let d = outInput.first {
                if !pm.updateValue(d, for: ic, onSubscribedCentrals: [s]) { return }
                outInput.removeFirst()
            }
        }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didPublishL2CAPChannel PSM: CBL2CAPPSM, error: Error?) {
        if error == nil { publishedPSM = PSM }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didUnpublishL2CAPChannel PSM: CBL2CAPPSM, error: Error?) {
        if PSM == publishedPSM { publishedPSM = 0 }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didOpen channel: CBL2CAPChannel?, error: Error?) {
        guard role == .host, connected, error == nil, let ch = channel else { return }
        DispatchQueue.main.async { self.openL2cap(ch) }
    }
}

// MARK: - Guest: CBCentralManagerDelegate

extension DunkDuelBLE: CBCentralManagerDelegate {
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        switch central.state {
        case .poweredOn:
            if scanRequested { beginScan() }
            if role == .guest, let id = joinTarget, remote == nil { connect(id) }
        case .poweredOff:
            if role == .guest {
                connected = false
                resetGuestLink()
                fail(.radioOff)
            }
        case .unauthorized:
            if role == .guest { fail(.permissionDenied) }
        case .unsupported:
            if role == .guest { fail(.internalError) }
        default:
            break
        }
    }

    private func beginScan() {
        guard let cm = centralManager, cm.state == .poweredOn, scanRequested else { return }
        cm.scanForPeripherals(
            withServices: [DunkDuelBLE.serviceUUID],
            options: [CBCentralManagerScanOptionAllowDuplicatesKey: true])
        if !connected { emitState(.scanning) }
    }

    private func connect(_ id: UUID) {
        guard let cm = centralManager else { return }
        if cm.isScanning { cm.stopScan() }
        scanRequested = false
        guard let target = discovered[id] ?? cm.retrievePeripherals(withIdentifiers: [id]).first else {
            fail(.connectFailed)
            return
        }
        remote = target
        target.delegate = self
        emitState(.connecting)
        connectAttempt &+= 1
        let attempt = connectAttempt
        cm.connect(target, options: nil)
        queue.asyncAfter(deadline: .now() + connectTimeout) { [weak self] in
            guard let self = self, self.connectAttempt == attempt, !self.connected, self.role == .guest,
                  let r = self.remote else { return }
            self.centralManager?.cancelPeripheralConnection(r)
            self.resetGuestLink()
            self.fail(.connectFailed)
        }
    }

    func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral,
                        advertisementData: [String: Any], rssi RSSI: NSNumber) {
        discovered[peripheral.identifier] = peripheral
        let now = ProcessInfo.processInfo.systemUptime
        if let last = lastPeerReport[peripheral.identifier], now - last < peerReportInterval { return }
        lastPeerReport[peripheral.identifier] = now
        let name = advertisementData[CBAdvertisementDataLocalNameKey] as? String
        let serviceData = (advertisementData[CBAdvertisementDataServiceDataKey] as? [CBUUID: Data])?[DunkDuelBLE.serviceUUID]
        emitPeer(peripheral.identifier, rssi: RSSI.intValue, name: name, serviceData: serviceData)
    }

    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        guard role == .guest, peripheral === remote else { return }
        peripheral.discoverServices([DunkDuelBLE.serviceUUID])
    }

    func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        guard role == .guest, peripheral === remote else { return }
        resetGuestLink()
        fail(.connectFailed)
    }

    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        guard role == .guest, peripheral === remote else { return }
        let was = connected
        connected = false
        resetGuestLink()
        outInput.removeAll()
        outControl.removeAll()
        DispatchQueue.main.async { self.closeL2cap() }
        if was { emitState(.disconnected) } else { fail(.connectFailed) }
    }
}

// MARK: - Guest: CBPeripheralDelegate

extension DunkDuelBLE: CBPeripheralDelegate {
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard role == .guest, peripheral === remote else { return }
        guard error == nil, let service = peripheral.services?.first(where: { $0.uuid == DunkDuelBLE.serviceUUID }) else {
            abortJoin(.protocolMismatch)
            return
        }
        peripheral.discoverCharacteristics([DunkDuelBLE.inputUUID, DunkDuelBLE.controlUUID, DunkDuelBLE.psmUUID], for: service)
    }

    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard role == .guest, peripheral === remote else { return }
        let chars = service.characteristics ?? []
        remoteInput = chars.first { $0.uuid == DunkDuelBLE.inputUUID }
        remoteControl = chars.first { $0.uuid == DunkDuelBLE.controlUUID }
        remotePSM = chars.first { $0.uuid == DunkDuelBLE.psmUUID }
        guard error == nil, let i = remoteInput, let c = remoteControl else {
            abortJoin(.protocolMismatch)
            return
        }
        peripheral.setNotifyValue(true, for: i)
        peripheral.setNotifyValue(true, for: c)
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        guard role == .guest, peripheral === remote else { return }
        if error != nil {
            abortJoin(.connectFailed)
            return
        }
        if characteristic.uuid == DunkDuelBLE.inputUUID { inputNotifying = characteristic.isNotifying }
        if characteristic.uuid == DunkDuelBLE.controlUUID { controlNotifying = characteristic.isNotifying }
        if inputNotifying && controlNotifying && !connected {
            connected = true
            everConnected = true
            connectAttempt &+= 1 // cancels the connect timeout
            // Fragment CONTROL at the unacknowledged size so iOS never falls back to long (prepared) writes.
            emitLink(maxPayload: peripheral.maximumWriteValueLength(for: .withoutResponse))
            emitState(.connected)
            if useL2cap, let psm = remotePSM { peripheral.readValue(for: psm) }
            flushGuest()
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard role == .guest, peripheral === remote, error == nil, let v = characteristic.value else { return }
        if characteristic.uuid == DunkDuelBLE.inputUUID {
            if connected { emit(Rec.input, v) }
        } else if characteristic.uuid == DunkDuelBLE.controlUUID {
            if connected { emit(Rec.control, v) }
        } else if characteristic.uuid == DunkDuelBLE.psmUUID, useL2cap, v.count >= 2 {
            let psm = UInt16(v[v.startIndex]) | (UInt16(v[v.startIndex + 1]) << 8)
            if psm != 0 { peripheral.openL2CAPChannel(CBL2CAPPSM(psm)) }
        }
    }

    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        guard role == .guest, peripheral === remote else { return }
        if characteristic.uuid == DunkDuelBLE.controlUUID {
            controlInFlight = false
            flushGuest()
        }
    }

    func peripheralIsReady(toSendWriteWithoutResponse peripheral: CBPeripheral) {
        guard role == .guest, peripheral === remote else { return }
        flushGuest()
    }

    func peripheral(_ peripheral: CBPeripheral, didOpen channel: CBL2CAPChannel?, error: Error?) {
        guard role == .guest, peripheral === remote, connected, error == nil, let ch = channel else { return }
        DispatchQueue.main.async { self.openL2cap(ch) }
    }

    private func flushGuest() {
        guard connected, let p = remote, let ic = remoteInput, let cc = remoteControl else { return }
        if !controlInFlight && !outControl.isEmpty {
            let d = outControl.removeFirst()
            controlInFlight = true
            p.writeValue(d, for: cc, type: .withResponse)
        }
        while !outInput.isEmpty && p.canSendWriteWithoutResponse {
            p.writeValue(outInput.removeFirst(), for: ic, type: .withoutResponse)
        }
    }

    private func abortJoin(_ e: LinkError) {
        if let r = remote { centralManager?.cancelPeripheralConnection(r) }
        connected = false
        resetGuestLink()
        fail(e)
    }
}

// MARK: - L2CAP (experimental; INPUT only, CONTROL always stays on GATT to keep its ordering)

extension DunkDuelBLE: StreamDelegate {
    private func openL2cap(_ ch: CBL2CAPChannel) {
        closeL2cap()
        l2capChannel = ch
        ch.inputStream.delegate = self
        ch.outputStream.delegate = self
        ch.inputStream.schedule(in: .main, forMode: .default)
        ch.outputStream.schedule(in: .main, forMode: .default)
        ch.inputStream.open()
        ch.outputStream.open()
        l2capReady = true
    }

    private func closeL2cap() {
        guard let ch = l2capChannel else { return }
        ch.inputStream.close()
        ch.outputStream.close()
        ch.inputStream.remove(from: .main, forMode: .default)
        ch.outputStream.remove(from: .main, forMode: .default)
        ch.inputStream.delegate = nil
        ch.outputStream.delegate = nil
        l2capChannel = nil
        l2capReady = false
        l2capIn.removeAll()
        l2capOut.removeAll()
    }

    func stream(_ aStream: Stream, handle eventCode: Stream.Event) {
        guard let ch = l2capChannel else { return }
        switch eventCode {
        case .hasBytesAvailable:
            var buf = [UInt8](repeating: 0, count: 1024)
            while ch.inputStream.hasBytesAvailable {
                let n = ch.inputStream.read(&buf, maxLength: buf.count)
                if n <= 0 { break }
                l2capIn.append(buf, count: n)
            }
            parseL2cap()
        case .hasSpaceAvailable:
            pumpL2cap()
        case .errorOccurred, .endEncountered:
            closeL2cap() // GATT keeps carrying INPUT
        default:
            break
        }
    }

    /// Stream framing: [u8 channel][u16 length LE][payload].
    private func parseL2cap() {
        while l2capIn.count >= 3 {
            let s = l2capIn.startIndex
            let len = Int(l2capIn[s + 1]) | (Int(l2capIn[s + 2]) << 8)
            if l2capIn.count < 3 + len { return }
            let channel = l2capIn[s]
            let payload = l2capIn.subdata(in: (s + 3)..<(s + 3 + len))
            l2capIn.removeSubrange(s..<(s + 3 + len))
            emit(channel == 0 ? Rec.input : Rec.control, payload)
        }
    }

    private func l2capEnqueue(channel: UInt8, data: Data) {
        if l2capOut.count > 64 * 23 { l2capOut.removeAll(); emitDropped(1) } // stalled stream: shed stale input
        l2capOut.append(channel)
        l2capOut.append(UInt8(truncatingIfNeeded: data.count))
        l2capOut.append(UInt8(truncatingIfNeeded: data.count >> 8))
        l2capOut.append(data)
        pumpL2cap()
    }

    private func pumpL2cap() {
        guard let ch = l2capChannel else { return }
        let out = ch.outputStream
        while !l2capOut.isEmpty && out.hasSpaceAvailable {
            let written = l2capOut.withUnsafeBytes { (raw: UnsafeRawBufferPointer) -> Int in
                guard let base = raw.bindMemory(to: UInt8.self).baseAddress else { return 0 }
                return out.write(base, maxLength: raw.count)
            }
            if written <= 0 { break }
            l2capOut.removeFirst(written)
        }
    }
}

// MARK: - C ABI (IosBleBackend.cs). All functions are called from the Unity main thread.

@_cdecl("dd_ble_init")
public func dd_ble_init(_ useL2cap: Int32) {
    DunkDuelBLE.shared.initialize(useL2cap: useL2cap != 0)
}

@_cdecl("dd_ble_can_host")
public func dd_ble_can_host() -> Int32 {
    return 1
}

@_cdecl("dd_ble_host")
public func dd_ble_host(_ name: UnsafePointer<UInt8>?, _ nameLength: Int32,
                        _ serviceData: UnsafePointer<UInt8>?, _ serviceDataLength: Int32) {
    // iOS advertises the local name; service data is for Android hosts and is ignored here.
    var localName = "DD-0000"
    if let n = name, nameLength > 0 {
        localName = String(decoding: UnsafeBufferPointer(start: n, count: Int(nameLength)), as: UTF8.self)
    }
    DunkDuelBLE.shared.host(name: localName)
}

@_cdecl("dd_ble_scan")
public func dd_ble_scan(_ enable: Int32) {
    DunkDuelBLE.shared.scan(enable != 0)
}

@_cdecl("dd_ble_join")
public func dd_ble_join(_ address: UnsafePointer<UInt8>?, _ length: Int32) {
    guard let a = address, length > 0 else { return }
    DunkDuelBLE.shared.join(address: String(decoding: UnsafeBufferPointer(start: a, count: Int(length)), as: UTF8.self))
}

@_cdecl("dd_ble_send")
public func dd_ble_send(_ channel: Int32, _ data: UnsafePointer<UInt8>?, _ offset: Int32, _ length: Int32) -> Int32 {
    guard let d = data, length > 0, offset >= 0 else { return 0 }
    return DunkDuelBLE.shared.send(channel: channel, data: Data(bytes: d.advanced(by: Int(offset)), count: Int(length)))
}

@_cdecl("dd_ble_poll")
public func dd_ble_poll(_ buffer: UnsafeMutablePointer<UInt8>?, _ capacity: Int32) -> Int32 {
    guard let b = buffer, capacity > 0 else { return 0 }
    return DunkDuelBLE.shared.drain(into: b, capacity: Int(capacity))
}

@_cdecl("dd_ble_disconnect")
public func dd_ble_disconnect() {
    DunkDuelBLE.shared.disconnect()
}

@_cdecl("dd_ble_shutdown")
public func dd_ble_shutdown() {
    DunkDuelBLE.shared.shutdown()
}
