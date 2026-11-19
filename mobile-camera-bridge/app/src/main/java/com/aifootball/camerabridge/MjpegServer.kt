package com.aifootball.camerabridge

import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 极小的 MJPEG HTTP 服务。它不做二次编码，只把 Camera2 的 JPEG 最新帧广播给
 * 每个客户端，因此桌面端可以直接使用 http://手机IP:8080/video 打开。
 */
class MjpegServer(
    private val port: Int,
    private val frames: LatestFrameStore,
    private val statusJson: () -> String,
) {
    private val running = AtomicBoolean(false)
    private val workers = Executors.newCachedThreadPool()
    private var serverSocket: ServerSocket? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        workers.execute {
            try {
                ServerSocket(port).use { server ->
                    serverSocket = server
                    while (running.get()) {
                        val socket = try { server.accept() } catch (_: Exception) { break }
                        workers.execute { serve(socket) }
                    }
                }
            } finally {
                serverSocket = null
                running.set(false)
            }
        }
    }

    fun stop() {
        running.set(false)
        try { serverSocket?.close() } catch (_: Exception) { }
        workers.shutdownNow()
    }

    private fun serve(socket: Socket) {
        socket.use {
            try {
                it.soTimeout = 4_000
                val input = BufferedInputStream(it.getInputStream())
                val request = input.readLineAscii() ?: return
                // Consume headers. They are not used, but this makes keep-alive clients harmless.
                while (true) {
                    val header = input.readLineAscii() ?: return
                    if (header.isEmpty()) break
                }
                val out = BufferedOutputStream(it.getOutputStream())
                when {
                    request.startsWith("GET /video ") || request.startsWith("GET /stream ") -> stream(out)
                    request.startsWith("GET /status ") -> respond(out, "200 OK", "application/json", statusJson())
                    request.startsWith("GET / ") -> respond(out, "200 OK", "text/plain; charset=utf-8",
                        "AIfootball Camera Bridge\n/video  MJPEG stream\n/status stream settings\n")
                    else -> respond(out, "404 Not Found", "text/plain", "Not found\n")
                }
            } catch (_: Exception) {
                // Broken/slow network clients must never stop capture or other clients.
            }
        }
    }

    private fun stream(out: BufferedOutputStream) {
        out.write(("HTTP/1.1 200 OK\r\n" +
            "Connection: close\r\nCache-Control: no-cache, no-store\r\n" +
            "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n\r\n").toByteArray())
        out.flush()
        var version = -1L
        while (running.get()) {
            val packet = frames.waitForNext(version, 2_000) ?: continue
            version = packet.version
            out.write("--frame\r\nContent-Type: image/jpeg\r\nContent-Length: ${packet.jpeg.size}\r\n\r\n".toByteArray())
            out.write(packet.jpeg)
            out.write("\r\n".toByteArray())
            out.flush()
        }
    }

    private fun respond(out: BufferedOutputStream, status: String, contentType: String, body: String) {
        val bytes = body.toByteArray(Charsets.UTF_8)
        out.write(("HTTP/1.1 $status\r\nConnection: close\r\nContent-Type: $contentType\r\n" +
            "Content-Length: ${bytes.size}\r\n\r\n").toByteArray())
        out.write(bytes)
        out.flush()
    }

    private fun BufferedInputStream.readLineAscii(): String? {
        val result = StringBuilder()
        while (result.length < 8_192) {
            val byte = read()
            if (byte == -1) return if (result.isEmpty()) null else result.toString()
            if (byte == '\n'.code) return result.toString().trimEnd('\r')
            result.append(byte.toChar())
        }
        return result.toString()
    }
}

class LatestFrameStore {
    data class Packet(val version: Long, val jpeg: ByteArray)

    private val lock = Object()
    private var latest: Packet? = null
    private var sequence = 0L

    fun publish(jpeg: ByteArray) {
        synchronized(lock) {
            sequence += 1
            latest = Packet(sequence, jpeg)
            lock.notifyAll()
        }
    }

    fun waitForNext(previousVersion: Long, timeoutMs: Long): Packet? = synchronized(lock) {
        if (latest?.version == previousVersion) lock.wait(timeoutMs)
        latest?.takeIf { it.version != previousVersion }
    }
}
