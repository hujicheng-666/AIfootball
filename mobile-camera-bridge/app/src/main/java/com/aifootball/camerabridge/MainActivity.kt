package com.aifootball.camerabridge

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.ImageFormat
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.ImageReader
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.util.Range
import android.util.Size
import android.view.Surface
import android.view.TextureView
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.LinearLayout
import android.widget.Spinner
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import java.net.NetworkInterface
import java.util.Collections
import java.util.Locale

/**
 * 手机端采集器：Camera2 会列出并使用设备真实支持的 JPEG 尺寸和 AE FPS 范围，
 * 不采用 IP Webcam 的预设档位。PC 端可继续填写标准 MJPEG 地址，无须改推理协议。
 */
class MainActivity : AppCompatActivity() {
    private val cameraManager by lazy { getSystemService(Context.CAMERA_SERVICE) as CameraManager }
    private val frames = LatestFrameStore()
    private var server: MjpegServer? = null
    private var workerThread: HandlerThread? = null
    private var worker: Handler? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null

    private lateinit var preview: TextureView
    private lateinit var sizeSpinner: Spinner
    private lateinit var fpsSpinner: Spinner
    private lateinit var startButton: Button
    private lateinit var status: TextView
    private lateinit var url: TextView
    private var selectedCameraId = ""
    private var sizes: List<Size> = emptyList()
    private var allFpsRanges: List<Range<Int>> = emptyList()
    private var fpsRanges: List<Range<Int>> = emptyList()
    private var jpegFpsCeilings: Map<Size, Int> = emptyMap()
    private var activeSize: Size? = null
    private var activeRange: Range<Int>? = null
    private var startedAt = 0L
    private var frameCount = 0

    private val cameraPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) initializeCameraOptions() else showStatus("需要相机权限才能开始采集")
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildUi())
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED)
            initializeCameraOptions()
        else cameraPermission.launch(Manifest.permission.CAMERA)
    }

    private fun buildUi(): LinearLayout {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(32, 24, 32, 24)
        }
        fun label(text: String) = TextView(this).apply { this.text = text; textSize = 15f }
        preview = TextureView(this).apply {
            layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f)
            isOpaque = true
        }
        sizeSpinner = Spinner(this)
        fpsSpinner = Spinner(this)
        startButton = Button(this).apply { text = "开始推流"; setOnClickListener { toggleStreaming() } }
        status = TextView(this).apply { textSize = 14f; setPadding(0, 16, 0, 0) }
        url = TextView(this).apply { textSize = 16f; setPadding(0, 8, 0, 8); setTextIsSelectable(true) }
        root.addView(label("分辨率（来自此手机后摄真实支持列表）"))
        root.addView(sizeSpinner)
        root.addView(label("目标帧率范围（Camera2 AE 支持范围）"))
        root.addView(fpsSpinner)
        root.addView(startButton)
        root.addView(url)
        root.addView(status)
        root.addView(preview)
        return root
    }

    private fun initializeCameraOptions() {
        try {
            selectedCameraId = cameraManager.cameraIdList.firstOrNull { id ->
                cameraManager.getCameraCharacteristics(id).get(CameraCharacteristics.LENS_FACING) ==
                    CameraCharacteristics.LENS_FACING_BACK
            } ?: throw IllegalStateException("未发现后置摄像头")
            val chars = cameraManager.getCameraCharacteristics(selectedCameraId)
            val map = chars.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
                ?: throw IllegalStateException("该相机未提供输出规格")
            sizes = (map.getOutputSizes(ImageFormat.JPEG)?.asList() ?: emptyList())
                .filter { it.width >= 640 && it.height >= 480 }
                .sortedWith(compareByDescending<Size> { it.width * it.height }.thenByDescending { it.width })
            allFpsRanges = (chars.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.asList() ?: emptyList())
                .sortedWith(compareByDescending<Range<Int>> { it.upper }.thenByDescending { it.lower })
            jpegFpsCeilings = sizes.associateWith { size ->
                val durationNs = map.getOutputMinFrameDuration(ImageFormat.JPEG, size)
                // 0 表示 HAL 未给出可靠上限；保留相机 AE 列表，而不是人为限速。
                if (durationNs <= 0) Int.MAX_VALUE else (1_000_000_000L / durationNs).toInt().coerceAtLeast(1)
            }
            if (sizes.isEmpty() || allFpsRanges.isEmpty()) throw IllegalStateException("相机没有可用 JPEG/FPS 档位")
            sizeSpinner.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item,
                sizes.map { "${it.width} × ${it.height}" })
            sizeSpinner.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
                override fun onNothingSelected(parent: AdapterView<*>?) = Unit
                override fun onItemSelected(parent: AdapterView<*>?, view: android.view.View?, position: Int, id: Long) {
                    populateFpsForSize(sizes[position])
                }
            }
            sizeSpinner.setSelection(sizes.indexOfFirst { it.width == 1920 && it.height == 1080 }.coerceAtLeast(0))
            url.text = "PC 填入：${localIp()}:8080/video"
            showStatus("已读取 ${sizes.size} 个分辨率；帧率会随所选分辨率过滤")
        } catch (e: Exception) {
            showStatus("初始化相机失败：${e.message}")
        }
    }

    /**
     * Camera2 的 AE 帧率范围是整个相机的能力，而 JPEG 输出尺寸有自身最小帧间隔。
     * 在界面切换分辨率时先按这个硬件上限筛选，避免展示明显不可能的 1080p/120 等组合。
     */
    private fun populateFpsForSize(size: Size) {
        val ceiling = jpegFpsCeilings[size] ?: Int.MAX_VALUE
        fpsRanges = allFpsRanges.filter { it.upper <= ceiling }.ifEmpty { allFpsRanges }
        fpsSpinner.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item,
            fpsRanges.map { "${it.lower}–${it.upper} fps" })
        fpsSpinner.setSelection(fpsRanges.indexOfFirst { it.upper >= 60 }.coerceAtLeast(0))
    }

    private fun toggleStreaming() {
        if (camera != null) stopStreaming() else startStreaming()
    }

    private fun startStreaming() {
        if (selectedCameraId.isEmpty()) return
        activeSize = sizes.getOrNull(sizeSpinner.selectedItemPosition) ?: return
        activeRange = fpsRanges.getOrNull(fpsSpinner.selectedItemPosition) ?: return
        sizeSpinner.isEnabled = false
        fpsSpinner.isEnabled = false
        startButton.isEnabled = false
        startWorker()
        server = MjpegServer(8080, frames) { statusJson() }.also { it.start() }
        try {
            cameraManager.openCamera(selectedCameraId, object : CameraDevice.StateCallback() {
                override fun onOpened(device: CameraDevice) {
                    camera = device
                    createSession(device)
                }
                override fun onDisconnected(device: CameraDevice) { device.close(); failAndStop("相机连接断开") }
                override fun onError(device: CameraDevice, error: Int) { device.close(); failAndStop("相机错误：$error") }
            }, worker)
        } catch (e: Exception) {
            failAndStop("无法打开相机：${e.message}")
        }
    }

    private fun createSession(device: CameraDevice) {
        val size = activeSize ?: return
        reader = ImageReader.newInstance(size.width, size.height, ImageFormat.JPEG, 3).also { imageReader ->
            imageReader.setOnImageAvailableListener({ r ->
                val image = r.acquireLatestImage() ?: return@setOnImageAvailableListener
                try {
                    val buffer = image.planes[0].buffer
                    val bytes = ByteArray(buffer.remaining())
                    buffer.get(bytes)
                    frames.publish(bytes)
                    frameCount += 1
                    if (frameCount % 30 == 0) runOnUiThread { updateRate() }
                } finally {
                    image.close()
                }
            }, worker)
        }
        val texture = preview.surfaceTexture
            ?: return failAndStop("预览界面尚未准备好，请稍后重试")
        texture.setDefaultBufferSize(size.width, size.height)
        val previewSurface = Surface(texture)
        val surfaces = listOf(previewSurface, reader!!.surface)
        device.createCaptureSession(surfaces, object : CameraCaptureSession.StateCallback() {
            override fun onConfigured(captureSession: CameraCaptureSession) {
                session = captureSession
                val request = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
                    addTarget(previewSurface)
                    addTarget(reader!!.surface)
                    set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
                    set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, activeRange)
                    set(CaptureRequest.JPEG_QUALITY, 88.toByte())
                }.build()
                try {
                    startedAt = System.currentTimeMillis(); frameCount = 0
                    captureSession.setRepeatingRequest(request, null, worker)
                    runOnUiThread {
                        startButton.isEnabled = true
                        startButton.text = "停止推流"
                        showStatus("正在以 ${size.width}×${size.height} / ${activeRange} 推流")
                    }
                } catch (e: Exception) { failAndStop("启动采集失败：${e.message}") }
            }
            override fun onConfigureFailed(captureSession: CameraCaptureSession) = failAndStop("该分辨率/帧率组合不被相机支持")
        }, worker)
    }

    private fun stopStreaming() {
        try { session?.stopRepeating() } catch (_: Exception) { }
        session?.close(); session = null
        reader?.close(); reader = null
        camera?.close(); camera = null
        server?.stop(); server = null
        workerThread?.quitSafely(); workerThread = null; worker = null
        startButton.text = "开始推流"; startButton.isEnabled = true
        sizeSpinner.isEnabled = true; fpsSpinner.isEnabled = true
        showStatus("推流已停止")
    }

    private fun failAndStop(message: String) = runOnUiThread { stopStreaming(); showStatus(message) }
    private fun startWorker() { workerThread = HandlerThread("camera-bridge").also { it.start() }; worker = Handler(workerThread!!.looper) }
    private fun updateRate() {
        val elapsed = (System.currentTimeMillis() - startedAt) / 1000.0
        if (elapsed > 0) showStatus(String.format(Locale.US, "推流中：实际 JPEG 输出约 %.1f fps", frameCount / elapsed))
    }
    private fun showStatus(message: String) { status.text = message }
    private fun statusJson(): String {
        val size = activeSize
        val range = activeRange
        return "{\"width\":${size?.width ?: 0},\"height\":${size?.height ?: 0},\"fpsMin\":${range?.lower ?: 0},\"fpsMax\":${range?.upper ?: 0}}"
    }
    private fun localIp(): String {
        return try {
            Collections.list(NetworkInterface.getNetworkInterfaces()).asSequence()
                .flatMap { Collections.list(it.inetAddresses).asSequence() }
                .firstOrNull { !it.isLoopbackAddress && it.hostAddress.indexOf(':') < 0 }
                ?.hostAddress?.let { "http://$it" } ?: "http://手机局域网IP"
        } catch (_: Exception) { "http://手机局域网IP" }
    }

    override fun onDestroy() { stopStreaming(); super.onDestroy() }
}
