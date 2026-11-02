// DunkDuelBle.kt
// Android BLE bridge for Dunk Duel. Called from Assets/DunkDuel/Transport.BLE/AndroidBleBackend.cs through
// AndroidJavaObject. Host = advertiser + GATT server; guest = scanner + GATT client.
//
// Threading: public methods are called from the Unity main thread and post onto a dedicated HandlerThread
// ("DunkDuelBle"); all Bluetooth callbacks post onto the same thread, so link state is single-threaded and
// records keep their order. poll() drains a ConcurrentLinkedQueue and may run on any thread.
//
// Wire constants mirror Assets/DunkDuel/Transport.BLE/BleProtocol.cs.
package com.dunkduel.ble

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattDescriptor
import android.bluetooth.BluetoothGattServer
import android.bluetooth.BluetoothGattServerCallback
import android.bluetooth.BluetoothGattService
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.bluetooth.BluetoothServerSocket
import android.bluetooth.BluetoothSocket
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.bluetooth.le.BluetoothLeAdvertiser
import android.bluetooth.le.BluetoothLeScanner
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.ParcelUuid
import android.os.SystemClock
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.ArrayDeque
import java.util.UUID
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.atomic.AtomicInteger

@SuppressLint("MissingPermission") // permissions are requested from C# (BlePermissions) before any call
@Suppress("DEPRECATION", "OVERRIDE_DEPRECATION")
class DunkDuelBle(context: Context) {

    companion object {
        @JvmField val SERVICE_UUID: UUID = UUID.fromString("dd0e0001-8e2a-4c1b-9d3f-6a5b4c3d2e1f")
        @JvmField val INPUT_UUID: UUID = UUID.fromString("dd0e0002-8e2a-4c1b-9d3f-6a5b4c3d2e1f")
        @JvmField val CONTROL_UUID: UUID = UUID.fromString("dd0e0003-8e2a-4c1b-9d3f-6a5b4c3d2e1f")
        @JvmField val PSM_UUID: UUID = UUID.fromString("dd0e0004-8e2a-4c1b-9d3f-6a5b4c3d2e1f")
        @JvmField val CCCD_UUID: UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")

        private const val REC_INPUT = 0
        private const val REC_CONTROL = 1
        private const val REC_STATE = 2
        private const val REC_PEER = 3
        private const val REC_LINK = 4
        private const val REC_CAN_HOST = 5
        private const val REC_DROPPED = 6

        // DunkDuel.Net.TransportState
        private const val ST_IDLE = 0
        private const val ST_HOSTING = 1
        private const val ST_SCANNING = 2
        private const val ST_CONNECTING = 3
        private const val ST_CONNECTED = 4
        private const val ST_DISCONNECTED = 5
        private const val ST_FAILED = 6

        // DunkDuel.Net.TransportError
        private const val ERR_NONE = 0
        private const val ERR_RADIO_OFF = 1
        private const val ERR_PERMISSION_DENIED = 2
        private const val ERR_ADVERTISING_UNSUPPORTED = 3
        private const val ERR_CONNECT_FAILED = 4
        private const val ERR_PROTOCOL_MISMATCH = 5
        private const val ERR_INTERNAL = 6

        private const val ROLE_NONE = 0
        private const val ROLE_HOST = 1
        private const val ROLE_GUEST = 2

        private const val STATUS_SUCCESS = 0 // BluetoothStatusCodes.SUCCESS (API 33 return codes)
        private const val MAX_INPUT_QUEUE = 4
        private const val MAX_RECORDS = 4096
        private const val MAX_POLL_RECORDS = 512
        private const val REQUESTED_MTU = 185
        private const val CONNECT_TIMEOUT_MS = 10_000L
        private const val MTU_FALLBACK_MS = 1_500L
        private const val BUSY_WATCHDOG_MS = 150L
        private const val RETRY_MS = 4L
        private const val PEER_THROTTLE_MS = 250L

        /** Best effort: open a client link back to the central so the peripheral can ask for a 7.5-15 ms interval. */
        private const val PERIPHERAL_PRIORITY_TRICK = true
    }

    private val appContext: Context = context.applicationContext
    private val manager: BluetoothManager? = appContext.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager
    private val adapter: BluetoothAdapter? = manager?.adapter
    private val thread = HandlerThread("DunkDuelBle").apply { start() }
    private val h = Handler(thread.looper)

    private val records = ConcurrentLinkedQueue<ByteArray>()
    private val recordCount = AtomicInteger(0)

    // ---- link state (HandlerThread unless @Volatile) ----
    private var role = ROLE_NONE
    @Volatile private var linkUp = false
    private var everConnected = false
    private var useL2cap = false
    private var mtuPayload = 20
    private val outInput = ArrayDeque<ByteArray>()
    private val outControl = ArrayDeque<ByteArray>()
    private var lastSentInput = false
    private var connectAttempt = 0

    // Host
    private var gattServer: BluetoothGattServer? = null
    private var advertiser: BluetoothLeAdvertiser? = null
    private var advertising = false
    private var serviceData = ByteArray(0)
    private var serverInput: BluetoothGattCharacteristic? = null
    private var serverControl: BluetoothGattCharacteristic? = null
    @Volatile private var hostAddress: String? = null
    private var hostDevice: BluetoothDevice? = null
    @Volatile private var inputSubscribed = false
    @Volatile private var controlSubscribed = false
    private var notifyBusy = false
    private var notifyBusySince = 0L
    private var priorityGatt: BluetoothGatt? = null
    private val prepared = HashMap<UUID, ByteArrayOutputStream>()

    // Guest
    private var scanner: BluetoothLeScanner? = null
    private var scanning = false
    private val lastPeerReport = HashMap<String, Long>()
    private var clientGatt: BluetoothGatt? = null
    private var clientInput: BluetoothGattCharacteristic? = null
    private var clientControl: BluetoothGattCharacteristic? = null
    private var clientPsm: BluetoothGattCharacteristic? = null
    private var discovering = false
    private var setupStep = 0
    private var gattBusy = false
    private var gattBusySince = 0L

    // L2CAP (experimental: INPUT only; CONTROL always stays on GATT to keep its ordering)
    @Volatile private var l2capActive = false
    @Volatile private var l2capPsm = 0
    private var l2capServer: BluetoothServerSocket? = null
    private var l2capSocket: BluetoothSocket? = null
    private var l2capWriter: Thread? = null
    private val l2capOut = LinkedBlockingQueue<ByteArray>()

    private var receiverRegistered = false

    // =============================================================== public API (Unity main thread)

    fun init(l2cap: Boolean): Boolean {
        h.post {
            useL2cap = l2cap && Build.VERSION.SDK_INT >= 29
            registerRadioReceiver()
            emitCanHost()
        }
        return adapter != null
    }

    fun canHost(): Boolean = computeCanHost()

    /** localName is unused on Android (an app cannot set a per-advertisement name); serviceData carries the court. */
    fun host(localName: String, serviceData: ByteArray) {
        val sd = serviceData.copyOf()
        h.post {
            teardown(false)
            role = ROLE_HOST
            this.serviceData = sd
            everConnected = false
            val a = adapter
            when {
                a == null -> fail(ERR_INTERNAL)
                !a.isEnabled -> fail(ERR_RADIO_OFF)
                !computeCanHost() -> fail(ERR_ADVERTISING_UNSUPPORTED)
                else -> guard { openServer() }
            }
        }
    }

    fun startScan() {
        h.post { startScanInternal() }
    }

    fun stopScan() {
        h.post {
            stopScanInternal()
            if (role == ROLE_GUEST && clientGatt == null && !linkUp) role = ROLE_NONE
        }
    }

    fun join(address: String) {
        h.post { joinInternal(address) }
    }

    /** Queues one INPUT packet (channel 0) or one CONTROL fragment (channel 1). */
    fun send(channel: Int, data: ByteArray): Boolean {
        if (channel == 0 && l2capActive) return l2capSend(data)
        h.post {
            if (!linkUp) return@post
            if (channel == 0) {
                outInput.addLast(data)
                var dropped = 0
                while (outInput.size > MAX_INPUT_QUEUE) {
                    outInput.pollFirst() // newer packets repeat the last 8 inputs
                    dropped++
                }
                if (dropped > 0) emitDropped(dropped)
            } else {
                outControl.addLast(data)
            }
            if (role == ROLE_HOST) pumpServer() else pumpClient()
        }
        return true
    }

    /** Every pending record as one byte[]: [u8 kind][u16 length LE][payload]...; null when empty. */
    fun poll(): ByteArray? {
        if (records.isEmpty()) return null
        val out = ByteArrayOutputStream(512)
        var n = 0
        while (n < MAX_POLL_RECORDS) {
            val r = records.poll() ?: break
            recordCount.decrementAndGet()
            out.write(r, 0, r.size)
            n++
        }
        return if (out.size() == 0) null else out.toByteArray()
    }

    fun disconnect() {
        h.post { teardown(true) }
    }

    fun shutdown() {
        h.post {
            teardown(false)
            if (receiverRegistered) {
                try {
                    appContext.unregisterReceiver(radioReceiver)
                } catch (e: Exception) {
                }
                receiverRegistered = false
            }
            thread.quitSafely()
        }
    }

    // =============================================================== records

    private fun emit(kind: Int, payload: ByteArray) {
        if (recordCount.get() >= MAX_RECORDS) return
        val r = ByteArray(3 + payload.size)
        r[0] = kind.toByte()
        r[1] = (payload.size and 0xFF).toByte()
        r[2] = ((payload.size shr 8) and 0xFF).toByte()
        System.arraycopy(payload, 0, r, 3, payload.size)
        records.add(r)
        recordCount.incrementAndGet()
    }

    private fun emitState(state: Int, error: Int = ERR_NONE) {
        emit(REC_STATE, byteArrayOf(state.toByte(), error.toByte()))
    }

    private fun fail(error: Int) {
        emitState(ST_FAILED, error)
    }

    private fun emitLink() {
        val p = mtuPayload.coerceIn(20, 512)
        emit(REC_LINK, byteArrayOf((p and 0xFF).toByte(), ((p shr 8) and 0xFF).toByte(), 0, 0, 0, 0))
    }

    private fun emitCanHost() {
        emit(REC_CAN_HOST, byteArrayOf((if (computeCanHost()) 1 else 0).toByte()))
    }

    private fun emitDropped(count: Int) {
        emit(REC_DROPPED, byteArrayOf(
            (count and 0xFF).toByte(), ((count shr 8) and 0xFF).toByte(),
            ((count shr 16) and 0xFF).toByte(), ((count shr 24) and 0xFF).toByte()))
    }

    private fun emitPeer(address: String, rssi: Int, name: String, serviceData: ByteArray) {
        val a = clip(address.toByteArray(Charsets.UTF_8))
        val n = clip(name.toByteArray(Charsets.UTF_8))
        val s = clip(serviceData)
        val out = ByteArrayOutputStream(4 + a.size + n.size + s.size)
        out.write(rssi.coerceIn(-128, 127) and 0xFF)
        out.write(a.size)
        out.write(a, 0, a.size)
        out.write(n.size)
        out.write(n, 0, n.size)
        out.write(s.size)
        out.write(s, 0, s.size)
        emit(REC_PEER, out.toByteArray())
    }

    private fun clip(b: ByteArray): ByteArray = if (b.size > 255) b.copyOf(255) else b

    private inline fun guard(block: () -> Unit) {
        try {
            block()
        } catch (e: SecurityException) {
            fail(ERR_PERMISSION_DENIED)
        } catch (e: Exception) {
            fail(ERR_INTERNAL)
        }
    }

    private fun computeCanHost(): Boolean = try {
        val a = adapter
        a != null && a.isEnabled && a.isMultipleAdvertisementSupported
    } catch (e: SecurityException) {
        false
    }

    // =============================================================== host: GATT server + advertiser

    private fun openServer() {
        val m = manager ?: return fail(ERR_INTERNAL)
        val server = m.openGattServer(appContext, serverCallback) ?: return fail(ERR_INTERNAL)
        gattServer = server
        val service = BluetoothGattService(SERVICE_UUID, BluetoothGattService.SERVICE_TYPE_PRIMARY)
        val input = BluetoothGattCharacteristic(
            INPUT_UUID,
            BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE or BluetoothGattCharacteristic.PROPERTY_NOTIFY,
            BluetoothGattCharacteristic.PERMISSION_WRITE)
        input.addDescriptor(BluetoothGattDescriptor(CCCD_UUID,
            BluetoothGattDescriptor.PERMISSION_READ or BluetoothGattDescriptor.PERMISSION_WRITE))
        val control = BluetoothGattCharacteristic(
            CONTROL_UUID,
            BluetoothGattCharacteristic.PROPERTY_WRITE or BluetoothGattCharacteristic.PROPERTY_INDICATE,
            BluetoothGattCharacteristic.PERMISSION_WRITE)
        control.addDescriptor(BluetoothGattDescriptor(CCCD_UUID,
            BluetoothGattDescriptor.PERMISSION_READ or BluetoothGattDescriptor.PERMISSION_WRITE))
        val psm = BluetoothGattCharacteristic(
            PSM_UUID, BluetoothGattCharacteristic.PROPERTY_READ, BluetoothGattCharacteristic.PERMISSION_READ)
        service.addCharacteristic(input)
        service.addCharacteristic(control)
        service.addCharacteristic(psm)
        serverInput = input
        serverControl = control
        if (!server.addService(service)) fail(ERR_INTERNAL) // advertising starts in onServiceAdded
    }

    private fun startAdvertising() {
        if (role != ROLE_HOST || advertising) return
        val a = adapter ?: return fail(ERR_INTERNAL)
        val adv = a.bluetoothLeAdvertiser ?: return fail(ERR_ADVERTISING_UNSUPPORTED)
        advertiser = adv
        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
            .setConnectable(true)
            .setTimeout(0)
            .build()
        // Main packet: flags + 128-bit service UUID (21 of 31 bytes). Scan response: service data (code, platform, name).
        val data = AdvertiseData.Builder()
            .setIncludeDeviceName(false)
            .setIncludeTxPowerLevel(false)
            .addServiceUuid(ParcelUuid(SERVICE_UUID))
            .build()
        val response = AdvertiseData.Builder()
            .setIncludeDeviceName(false)
            .setIncludeTxPowerLevel(false)
            .addServiceData(ParcelUuid(SERVICE_UUID), serviceData)
            .build()
        guard {
            adv.startAdvertising(settings, data, response, advertiseCallback)
            advertising = true
        }
    }

    private fun stopAdvertising() {
        if (!advertising) return
        advertising = false
        try {
            advertiser?.stopAdvertising(advertiseCallback)
        } catch (e: Exception) {
        }
    }

    private val advertiseCallback = object : AdvertiseCallback() {
        override fun onStartSuccess(settingsInEffect: AdvertiseSettings?) {
            h.post { if (role == ROLE_HOST && !everConnected) emitState(ST_HOSTING) }
        }

        override fun onStartFailure(errorCode: Int) {
            h.post {
                if (errorCode == ADVERTISE_FAILED_ALREADY_STARTED) return@post
                advertising = false
                if (role == ROLE_HOST) {
                    fail(if (errorCode == ADVERTISE_FAILED_FEATURE_UNSUPPORTED) ERR_ADVERTISING_UNSUPPORTED else ERR_INTERNAL)
                }
            }
        }
    }

    private val serverCallback = object : BluetoothGattServerCallback() {
        override fun onServiceAdded(status: Int, service: BluetoothGattService) {
            h.post {
                if (role != ROLE_HOST) return@post
                if (status != BluetoothGatt.GATT_SUCCESS) {
                    fail(ERR_INTERNAL)
                    return@post
                }
                if (useL2cap) openL2capServer()
                startAdvertising()
            }
        }

        override fun onConnectionStateChange(device: BluetoothDevice, status: Int, newState: Int) {
            h.post { serverConnectionChanged(device, newState) }
        }

        override fun onCharacteristicWriteRequest(
            device: BluetoothDevice, requestId: Int, characteristic: BluetoothGattCharacteristic,
            preparedWrite: Boolean, responseNeeded: Boolean, offset: Int, value: ByteArray?
        ) {
            val data = value ?: ByteArray(0)
            val ours = device.address == hostAddress
            val kind = when (characteristic.uuid) {
                INPUT_UUID -> REC_INPUT
                CONTROL_UUID -> REC_CONTROL
                else -> -1
            }
            if (ours && kind >= 0) {
                if (preparedWrite) {
                    appendPrepared(characteristic.uuid, offset, data)
                } else {
                    val address = device.address
                    h.post { if (linkUp && address == hostAddress) emit(kind, data) }
                }
            }
            if (responseNeeded) {
                respond(device, requestId,
                    if (ours && kind >= 0) BluetoothGatt.GATT_SUCCESS else BluetoothGatt.GATT_WRITE_NOT_PERMITTED,
                    offset, data)
            }
        }

        override fun onExecuteWrite(device: BluetoothDevice, requestId: Int, execute: Boolean) {
            val writes = takePrepared()
            if (execute && device.address == hostAddress) {
                val address = device.address
                h.post {
                    if (!linkUp || address != hostAddress) return@post
                    for (w in writes) emit(if (w.first == INPUT_UUID) REC_INPUT else REC_CONTROL, w.second)
                }
            }
            respond(device, requestId, BluetoothGatt.GATT_SUCCESS, 0, null)
        }

        override fun onDescriptorWriteRequest(
            device: BluetoothDevice, requestId: Int, descriptor: BluetoothGattDescriptor,
            preparedWrite: Boolean, responseNeeded: Boolean, offset: Int, value: ByteArray?
        ) {
            val v = value ?: ByteArray(0)
            val uuid = descriptor.characteristic?.uuid
            if (responseNeeded) respond(device, requestId, BluetoothGatt.GATT_SUCCESS, offset, v)
            h.post { cccdWritten(device, uuid, v) }
        }

        override fun onDescriptorReadRequest(device: BluetoothDevice, requestId: Int, offset: Int, descriptor: BluetoothGattDescriptor) {
            val uuid = descriptor.characteristic?.uuid
            val value = when {
                uuid == INPUT_UUID && inputSubscribed -> BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE
                uuid == CONTROL_UUID && controlSubscribed -> BluetoothGattDescriptor.ENABLE_INDICATION_VALUE
                else -> BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE
            }
            respond(device, requestId, BluetoothGatt.GATT_SUCCESS, 0, value)
        }

        override fun onCharacteristicReadRequest(device: BluetoothDevice, requestId: Int, offset: Int, characteristic: BluetoothGattCharacteristic) {
            if (characteristic.uuid != PSM_UUID) {
                respond(device, requestId, BluetoothGatt.GATT_READ_NOT_PERMITTED, offset, null)
                return
            }
            val psm = l2capPsm
            val v = byteArrayOf((psm and 0xFF).toByte(), ((psm shr 8) and 0xFF).toByte())
            if (offset > v.size) respond(device, requestId, BluetoothGatt.GATT_INVALID_OFFSET, offset, null)
            else respond(device, requestId, BluetoothGatt.GATT_SUCCESS, offset, v.copyOfRange(offset, v.size))
        }

        override fun onNotificationSent(device: BluetoothDevice, status: Int) {
            h.post {
                notifyBusy = false
                pumpServer()
            }
        }

        override fun onMtuChanged(device: BluetoothDevice, mtu: Int) {
            val address = device.address
            h.post {
                if (address != hostAddress) return@post
                mtuPayload = mtu - 3
                if (linkUp) emitLink()
            }
        }
    }

    private fun respond(device: BluetoothDevice, requestId: Int, status: Int, offset: Int, value: ByteArray?) {
        try {
            gattServer?.sendResponse(device, requestId, status, offset, value)
        } catch (e: Exception) {
        }
    }

    private fun appendPrepared(uuid: UUID, offset: Int, data: ByteArray) {
        synchronized(prepared) {
            val buf = prepared.getOrPut(uuid) { ByteArrayOutputStream() }
            if (offset == buf.size()) buf.write(data, 0, data.size) // out-of-order prepared writes are dropped
        }
    }

    private fun takePrepared(): List<Pair<UUID, ByteArray>> = synchronized(prepared) {
        val list = prepared.map { Pair(it.key, it.value.toByteArray()) }
        prepared.clear()
        list
    }

    private fun serverConnectionChanged(device: BluetoothDevice, newState: Int) {
        if (role != ROLE_HOST) return
        if (newState == BluetoothProfile.STATE_CONNECTED) {
            val current = hostAddress
            if (current == null) {
                hostAddress = device.address
                hostDevice = device
                inputSubscribed = false
                controlSubscribed = false
            } else if (current != device.address) {
                try {
                    gattServer?.cancelConnection(device) // one guest per court
                } catch (e: Exception) {
                }
            }
        } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
            if (device.address != hostAddress) return
            val was = linkUp
            dropHostLink()
            if (was) emitState(ST_DISCONNECTED)
            startAdvertising() // let the same guest reconnect inside the session's window
        }
    }

    private fun cccdWritten(device: BluetoothDevice, uuid: UUID?, value: ByteArray) {
        if (role != ROLE_HOST || device.address != hostAddress) return
        val on = value.isNotEmpty() && (value[0].toInt() and 0x03) != 0
        when (uuid) {
            INPUT_UUID -> inputSubscribed = on
            CONTROL_UUID -> controlSubscribed = on
        }
        if (inputSubscribed && controlSubscribed && !linkUp) {
            linkUp = true
            everConnected = true
            stopAdvertising()
            if (PERIPHERAL_PRIORITY_TRICK) requestPriorityAsPeripheral(device)
            emitLink()
            emitState(ST_CONNECTED)
            pumpServer()
        } else if (!on && linkUp) {
            dropHostLink()
            emitState(ST_DISCONNECTED)
            startAdvertising()
        }
    }

    private fun pumpServer() {
        val server = gattServer ?: return
        val device = hostDevice ?: return
        if (!linkUp) return
        if (notifyBusy) {
            if (SystemClock.uptimeMillis() - notifyBusySince < BUSY_WATCHDOG_MS) return
            notifyBusy = false // some stacks drop onNotificationSent
        }
        val useControl = outControl.isNotEmpty() && (outInput.isEmpty() || lastSentInput)
        val queue = if (useControl) outControl else outInput
        val data = queue.peekFirst() ?: return
        val ch = (if (useControl) serverControl else serverInput) ?: return
        if (notify(server, device, ch, data, useControl)) {
            queue.pollFirst()
            notifyBusy = true
            notifyBusySince = SystemClock.uptimeMillis()
            lastSentInput = !useControl
        } else {
            h.postDelayed({ pumpServer() }, RETRY_MS)
        }
    }

    private fun notify(server: BluetoothGattServer, device: BluetoothDevice, ch: BluetoothGattCharacteristic, data: ByteArray, confirm: Boolean): Boolean {
        return try {
            if (Build.VERSION.SDK_INT >= 33) {
                server.notifyCharacteristicChanged(device, ch, confirm, data) == STATUS_SUCCESS
            } else {
                ch.value = data
                server.notifyCharacteristicChanged(device, ch, confirm)
            }
        } catch (e: Exception) {
            false
        }
    }

    private fun requestPriorityAsPeripheral(device: BluetoothDevice) {
        closePriorityGatt()
        try {
            priorityGatt = device.connectGatt(appContext, false, priorityCallback, BluetoothDevice.TRANSPORT_LE)
        } catch (e: Exception) {
            priorityGatt = null
        }
    }

    private val priorityCallback = object : BluetoothGattCallback() {
        override fun onConnectionStateChange(gatt: BluetoothGatt, status: Int, newState: Int) {
            if (newState != BluetoothProfile.STATE_CONNECTED) return
            try {
                gatt.requestConnectionPriority(BluetoothGatt.CONNECTION_PRIORITY_HIGH)
            } catch (e: Exception) {
            }
        }
    }

    private fun closePriorityGatt() {
        val g = priorityGatt ?: return
        priorityGatt = null
        try {
            g.close()
        } catch (e: Exception) {
        }
    }

    private fun dropHostLink() {
        linkUp = false
        hostAddress = null
        hostDevice = null
        inputSubscribed = false
        controlSubscribed = false
        notifyBusy = false
        outInput.clear()
        outControl.clear()
        takePrepared()
        closePriorityGatt()
        closeL2cap()
    }

    // =============================================================== guest: scanner + GATT client

    private fun startScanInternal() {
        if (scanning) return
        val a = adapter ?: return fail(ERR_INTERNAL)
        if (!a.isEnabled) return fail(ERR_RADIO_OFF)
        val sc = a.bluetoothLeScanner ?: return fail(ERR_RADIO_OFF)
        scanner = sc
        synchronized(lastPeerReport) { lastPeerReport.clear() }
        val filters = listOf(ScanFilter.Builder().setServiceUuid(ParcelUuid(SERVICE_UUID)).build())
        val settings = ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build()
        guard {
            sc.startScan(filters, settings, scanCallback)
            scanning = true
            if (role == ROLE_NONE) role = ROLE_GUEST
            emitState(ST_SCANNING)
        }
    }

    private fun stopScanInternal() {
        if (!scanning) return
        scanning = false
        try {
            scanner?.stopScan(scanCallback)
        } catch (e: Exception) {
        }
    }

    private val scanCallback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult) {
            report(result)
        }

        override fun onBatchScanResults(results: MutableList<ScanResult>) {
            for (r in results) report(r)
        }

        override fun onScanFailed(errorCode: Int) {
            h.post {
                scanning = false
                fail(ERR_INTERNAL)
            }
        }
    }

    private fun report(result: ScanResult) {
        val address = result.device?.address ?: return
        val now = SystemClock.elapsedRealtime()
        synchronized(lastPeerReport) {
            val last = lastPeerReport[address]
            if (last != null && now - last < PEER_THROTTLE_MS) return
            lastPeerReport[address] = now
        }
        val record = result.scanRecord
        val name = record?.deviceName ?: ""
        val sd = record?.getServiceData(ParcelUuid(SERVICE_UUID)) ?: ByteArray(0)
        emitPeer(address, result.rssi, name, sd)
    }

    private fun joinInternal(address: String) {
        teardown(false)
        role = ROLE_GUEST
        everConnected = false
        val a = adapter ?: return fail(ERR_INTERNAL)
        if (!a.isEnabled) return fail(ERR_RADIO_OFF)
        val device = try {
            a.getRemoteDevice(address)
        } catch (e: IllegalArgumentException) {
            null
        } ?: return fail(ERR_CONNECT_FAILED)
        emitState(ST_CONNECTING)
        guard {
            clientGatt = device.connectGatt(appContext, false, clientCallback, BluetoothDevice.TRANSPORT_LE)
            if (clientGatt == null) fail(ERR_CONNECT_FAILED)
        }
        val attempt = ++connectAttempt
        h.postDelayed({
            if (connectAttempt == attempt && !linkUp && role == ROLE_GUEST) {
                closeClient()
                fail(ERR_CONNECT_FAILED)
            }
        }, CONNECT_TIMEOUT_MS)
    }

    private val clientCallback = object : BluetoothGattCallback() {
        override fun onConnectionStateChange(gatt: BluetoothGatt, status: Int, newState: Int) {
            h.post { clientConnectionChanged(gatt, status, newState) }
        }

        override fun onMtuChanged(gatt: BluetoothGatt, mtu: Int, status: Int) {
            h.post {
                if (gatt != clientGatt) return@post
                if (status == BluetoothGatt.GATT_SUCCESS) mtuPayload = mtu - 3
                discover(gatt)
            }
        }

        override fun onServicesDiscovered(gatt: BluetoothGatt, status: Int) {
            h.post { servicesDiscovered(gatt, status) }
        }

        override fun onDescriptorWrite(gatt: BluetoothGatt, descriptor: BluetoothGattDescriptor, status: Int) {
            h.post { descriptorWritten(gatt, status) }
        }

        override fun onCharacteristicWrite(gatt: BluetoothGatt, characteristic: BluetoothGattCharacteristic, status: Int) {
            h.post {
                if (gatt != clientGatt) return@post
                gattBusy = false
                pumpClient()
            }
        }

        // API 33+
        override fun onCharacteristicChanged(gatt: BluetoothGatt, characteristic: BluetoothGattCharacteristic, value: ByteArray) {
            received(gatt, characteristic.uuid, value)
        }

        // API 32 and older (the characteristic's value is shared: copy it now)
        override fun onCharacteristicChanged(gatt: BluetoothGatt, characteristic: BluetoothGattCharacteristic) {
            received(gatt, characteristic.uuid, characteristic.value?.copyOf() ?: ByteArray(0))
        }

        // API 33+
        override fun onCharacteristicRead(gatt: BluetoothGatt, characteristic: BluetoothGattCharacteristic, value: ByteArray, status: Int) {
            val uuid = characteristic.uuid
            h.post { characteristicRead(gatt, uuid, value, status) }
        }

        // API 32 and older
        override fun onCharacteristicRead(gatt: BluetoothGatt, characteristic: BluetoothGattCharacteristic, status: Int) {
            val uuid = characteristic.uuid
            val value = characteristic.value?.copyOf() ?: ByteArray(0)
            h.post { characteristicRead(gatt, uuid, value, status) }
        }
    }

    private fun received(gatt: BluetoothGatt, uuid: UUID, value: ByteArray) {
        // Hop to the link thread so data never overtakes the Connected record.
        h.post {
            if (gatt != clientGatt || !linkUp) return@post
            when (uuid) {
                INPUT_UUID -> emit(REC_INPUT, value)
                CONTROL_UUID -> emit(REC_CONTROL, value)
            }
        }
    }

    private fun clientConnectionChanged(gatt: BluetoothGatt, status: Int, newState: Int) {
        if (gatt != clientGatt) {
            try {
                gatt.close()
            } catch (e: Exception) {
            }
            return
        }
        if (newState == BluetoothProfile.STATE_CONNECTED && status == BluetoothGatt.GATT_SUCCESS) {
            discovering = false
            guard {
                gatt.requestConnectionPriority(BluetoothGatt.CONNECTION_PRIORITY_HIGH)
                if (!gatt.requestMtu(REQUESTED_MTU)) {
                    discover(gatt)
                } else {
                    h.postDelayed({ if (gatt == clientGatt && !discovering) discover(gatt) }, MTU_FALLBACK_MS)
                }
            }
        } else {
            val was = linkUp
            closeClient()
            if (was) emitState(ST_DISCONNECTED) else fail(ERR_CONNECT_FAILED)
        }
    }

    private fun discover(gatt: BluetoothGatt) {
        if (discovering || gatt != clientGatt) return
        discovering = true
        guard {
            if (!gatt.discoverServices()) {
                closeClient()
                fail(ERR_CONNECT_FAILED)
            }
        }
    }

    private fun servicesDiscovered(gatt: BluetoothGatt, status: Int) {
        if (gatt != clientGatt) return
        val service = gatt.getService(SERVICE_UUID)
        val input = service?.getCharacteristic(INPUT_UUID)
        val control = service?.getCharacteristic(CONTROL_UUID)
        if (status != BluetoothGatt.GATT_SUCCESS || service == null || input == null || control == null) {
            closeClient()
            fail(ERR_PROTOCOL_MISMATCH)
            return
        }
        clientInput = input
        clientControl = control
        clientPsm = service.getCharacteristic(PSM_UUID)
        setupStep = 0
        nextSetupStep(gatt)
    }

    private fun nextSetupStep(gatt: BluetoothGatt) {
        when (setupStep) {
            0 -> enableCccd(gatt, clientInput, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE)
            1 -> enableCccd(gatt, clientControl, BluetoothGattDescriptor.ENABLE_INDICATION_VALUE)
            else -> {
                linkUp = true
                everConnected = true
                connectAttempt++ // cancels the connect timeout
                emitLink()
                emitState(ST_CONNECTED)
                val psm = clientPsm
                if (useL2cap && psm != null) readCharacteristic(gatt, psm) else pumpClient()
            }
        }
    }

    private fun enableCccd(gatt: BluetoothGatt, ch: BluetoothGattCharacteristic?, value: ByteArray) {
        if (ch == null) {
            closeClient()
            fail(ERR_PROTOCOL_MISMATCH)
            return
        }
        guard {
            gatt.setCharacteristicNotification(ch, true)
            val d = ch.getDescriptor(CCCD_UUID)
            if (d == null) {
                setupStep++
                nextSetupStep(gatt)
                return@guard
            }
            val ok = if (Build.VERSION.SDK_INT >= 33) {
                gatt.writeDescriptor(d, value) == STATUS_SUCCESS
            } else {
                d.value = value
                gatt.writeDescriptor(d)
            }
            if (!ok) {
                closeClient()
                fail(ERR_CONNECT_FAILED)
            }
        }
    }

    private fun descriptorWritten(gatt: BluetoothGatt, status: Int) {
        if (gatt != clientGatt || linkUp) return
        if (status != BluetoothGatt.GATT_SUCCESS) {
            closeClient()
            fail(ERR_CONNECT_FAILED)
            return
        }
        setupStep++
        nextSetupStep(gatt)
    }

    private fun readCharacteristic(gatt: BluetoothGatt, ch: BluetoothGattCharacteristic) {
        guard {
            if (gatt.readCharacteristic(ch)) {
                gattBusy = true
                gattBusySince = SystemClock.uptimeMillis()
            } else {
                pumpClient()
            }
        }
    }

    private fun characteristicRead(gatt: BluetoothGatt, uuid: UUID, value: ByteArray, status: Int) {
        if (gatt != clientGatt) return
        gattBusy = false
        if (uuid == PSM_UUID && status == BluetoothGatt.GATT_SUCCESS && value.size >= 2 && useL2cap) {
            val psm = (value[0].toInt() and 0xFF) or ((value[1].toInt() and 0xFF) shl 8)
            if (psm != 0) openL2capClient(gatt.device, psm)
        }
        pumpClient()
    }

    private fun pumpClient() {
        val gatt = clientGatt ?: return
        if (!linkUp) return
        if (gattBusy) {
            if (SystemClock.uptimeMillis() - gattBusySince < BUSY_WATCHDOG_MS) return
            gattBusy = false // some stacks never call back for write-without-response
        }
        val useControl = outControl.isNotEmpty() && (outInput.isEmpty() || lastSentInput)
        val queue = if (useControl) outControl else outInput
        val data = queue.peekFirst() ?: return
        val ch = (if (useControl) clientControl else clientInput) ?: return
        val type = if (useControl) BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT else BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE
        if (write(gatt, ch, data, type)) {
            queue.pollFirst()
            gattBusy = true
            gattBusySince = SystemClock.uptimeMillis()
            lastSentInput = !useControl
        } else {
            h.postDelayed({ pumpClient() }, RETRY_MS)
        }
    }

    private fun write(gatt: BluetoothGatt, ch: BluetoothGattCharacteristic, data: ByteArray, type: Int): Boolean {
        return try {
            if (Build.VERSION.SDK_INT >= 33) {
                gatt.writeCharacteristic(ch, data, type) == STATUS_SUCCESS
            } else {
                ch.writeType = type
                ch.value = data
                gatt.writeCharacteristic(ch)
            }
        } catch (e: Exception) {
            false
        }
    }

    private fun closeClient() {
        val g = clientGatt
        clientGatt = null
        linkUp = false
        clientInput = null
        clientControl = null
        clientPsm = null
        discovering = false
        setupStep = 0
        gattBusy = false
        outInput.clear()
        outControl.clear()
        closeL2cap()
        if (g != null) {
            try {
                g.disconnect()
                g.close()
            } catch (e: Exception) {
            }
        }
    }

    // =============================================================== teardown and radio state

    private fun teardown(emitIdle: Boolean) {
        connectAttempt++
        stopScanInternal()
        stopAdvertising()
        closeClient()
        val d = hostDevice
        if (d != null) {
            try {
                gattServer?.cancelConnection(d)
            } catch (e: Exception) {
            }
        }
        dropHostLink()
        closeL2capServer()
        try {
            gattServer?.close()
        } catch (e: Exception) {
        }
        gattServer = null
        serverInput = null
        serverControl = null
        role = ROLE_NONE
        if (emitIdle) emitState(ST_IDLE)
    }

    private val radioReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (intent.action != BluetoothAdapter.ACTION_STATE_CHANGED) return
            val state = intent.getIntExtra(BluetoothAdapter.EXTRA_STATE, BluetoothAdapter.ERROR)
            h.post {
                when (state) {
                    BluetoothAdapter.STATE_OFF -> {
                        val active = role != ROLE_NONE
                        teardown(false)
                        if (active) fail(ERR_RADIO_OFF)
                        emitCanHost()
                    }
                    BluetoothAdapter.STATE_ON -> emitCanHost()
                }
            }
        }
    }

    private fun registerRadioReceiver() {
        if (receiverRegistered) return
        val filter = IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED)
        try {
            if (Build.VERSION.SDK_INT >= 33) {
                appContext.registerReceiver(radioReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
            } else {
                appContext.registerReceiver(radioReceiver, filter)
            }
            receiverRegistered = true
        } catch (e: Exception) {
        }
    }

    // =============================================================== L2CAP (API 29+, experimental)

    private fun openL2capServer() {
        if (Build.VERSION.SDK_INT < 29) return
        val a = adapter ?: return
        try {
            val server = a.listenUsingInsecureL2capChannel()
            l2capServer = server
            l2capPsm = server.psm
            Thread({
                try {
                    val socket = server.accept()
                    h.post { attachL2cap(socket) }
                } catch (e: IOException) {
                    // server closed
                }
            }, "DunkDuelBle-l2cap-accept").start()
        } catch (e: Exception) {
            l2capPsm = 0
        }
    }

    private fun openL2capClient(device: BluetoothDevice, psm: Int) {
        if (Build.VERSION.SDK_INT < 29) return
        Thread({
            try {
                val socket = device.createInsecureL2capChannel(psm)
                socket.connect()
                h.post { attachL2cap(socket) }
            } catch (e: Exception) {
                // GATT keeps carrying INPUT
            }
        }, "DunkDuelBle-l2cap-connect").start()
    }

    private fun attachL2cap(socket: BluetoothSocket) {
        if (!linkUp) {
            try {
                socket.close()
            } catch (e: IOException) {
            }
            return
        }
        closeL2cap()
        l2capSocket = socket
        l2capOut.clear()
        val input = socket.inputStream
        val output = socket.outputStream
        l2capActive = true
        Thread({ readL2cap(input) }, "DunkDuelBle-l2cap-read").start()
        l2capWriter = Thread({ writeL2cap(output) }, "DunkDuelBle-l2cap-write").also { it.start() }
    }

    /** Stream framing: [u8 channel][u16 length LE][payload]. */
    private fun readL2cap(input: InputStream) {
        val header = ByteArray(3)
        try {
            while (true) {
                readFully(input, header, 3)
                val len = (header[1].toInt() and 0xFF) or ((header[2].toInt() and 0xFF) shl 8)
                val payload = ByteArray(len)
                readFully(input, payload, len)
                val kind = if (header[0].toInt() == 0) REC_INPUT else REC_CONTROL
                h.post { if (linkUp) emit(kind, payload) }
            }
        } catch (e: IOException) {
            h.post { closeL2cap() }
        }
    }

    private fun readFully(input: InputStream, buf: ByteArray, n: Int) {
        var off = 0
        while (off < n) {
            val r = input.read(buf, off, n - off)
            if (r < 0) throw IOException("L2CAP channel closed")
            off += r
        }
    }

    private fun writeL2cap(output: OutputStream) {
        try {
            while (true) {
                val frame = l2capOut.take()
                output.write(frame)
                output.flush()
            }
        } catch (e: InterruptedException) {
            // closing
        } catch (e: IOException) {
            h.post { closeL2cap() }
        }
    }

    private fun l2capSend(data: ByteArray): Boolean {
        val frame = ByteArray(3 + data.size)
        frame[0] = 0
        frame[1] = (data.size and 0xFF).toByte()
        frame[2] = ((data.size shr 8) and 0xFF).toByte()
        System.arraycopy(data, 0, frame, 3, data.size)
        while (l2capOut.size >= MAX_INPUT_QUEUE) {
            if (l2capOut.poll() != null) emitDropped(1)
        }
        return l2capOut.offer(frame)
    }

    private fun closeL2cap() {
        l2capActive = false
        l2capWriter?.interrupt()
        l2capWriter = null
        try {
            l2capSocket?.close()
        } catch (e: IOException) {
        }
        l2capSocket = null
        l2capOut.clear()
    }

    private fun closeL2capServer() {
        try {
            l2capServer?.close()
        } catch (e: IOException) {
        }
        l2capServer = null
        l2capPsm = 0
    }
}
