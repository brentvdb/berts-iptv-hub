package com.berts.iptvhub

import okhttp3.OkHttpClient
import java.net.Inet4Address
import java.net.NetworkInterface
import java.util.concurrent.Callable
import java.util.concurrent.Executors
import java.util.concurrent.Future
import java.util.concurrent.TimeUnit

/** Finds Bert's IPTV Hub on the local network by probing port 8090 across the box's own subnet. */
object Discovery {
    const val DEFAULT_PORT = 8090

    data class Found(val baseUrl: String, val info: HubInfo)

    fun scan(base: OkHttpClient, port: Int = DEFAULT_PORT): List<Found> {
        val fast = base.newBuilder()
            .connectTimeout(450, TimeUnit.MILLISECONDS)
            .readTimeout(900, TimeUnit.MILLISECONDS)
            .callTimeout(1500, TimeUnit.MILLISECONDS)
            .retryOnConnectionFailure(false)
            .build()
        val pool = Executors.newFixedThreadPool(48)
        try {
            val futures = ArrayList<Future<Found?>>()
            for (prefix in localPrefixes()) {
                for (host in 1..254) {
                    val url = "http://$prefix$host:$port"
                    futures.add(pool.submit(Callable<Found?> { HubApi.probe(fast, url)?.let { Found(url, it) } }))
                }
            }
            return futures.mapNotNull { f -> runCatching { f.get(10, TimeUnit.SECONDS) }.getOrNull() }
        } finally {
            pool.shutdownNow()
        }
    }

    /** e.g. "192.168.1." for every private IPv4 network the box is on. */
    fun localPrefixes(): List<String> = try {
        NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .filterIsInstance<Inet4Address>()
            .filter { it.isSiteLocalAddress }
            .mapNotNull { it.hostAddress?.substringBeforeLast('.')?.plus(".") }
            .distinct()
    } catch (_: Exception) {
        emptyList()
    }
}
