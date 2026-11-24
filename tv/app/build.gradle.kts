plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// One version for the whole system: <repo>/VERSION (shared with the hub server).
val appVersion: String = rootProject.file("../VERSION").readText().trim()
// CI passes its run number so every build installs over the previous one.
val buildNumber: Int = (System.getenv("BUILD_NUMBER") ?: System.getenv("GITHUB_RUN_NUMBER"))?.toIntOrNull() ?: 1

android {
    namespace = "com.berts.iptvhub"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.berts.iptvhub.tv"
        minSdk = 23
        targetSdk = 35
        versionCode = buildNumber
        versionName = appVersion

        // TV boxes are ARM; leaving out x86 keeps the built-in VLC engine from doubling the APK size.
        ndk {
            abiFilters += listOf("armeabi-v7a", "arm64-v8a")
        }
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true // compress libVLC inside the APK (smaller download)
        }
    }

    // Fixed key kept in the repo so every build (local or CI) installs over the last one.
    // This is a private sideload app; swap for a secret-backed key before any store release.
    signingConfigs {
        create("hub") {
            storeFile = rootProject.file("signing/berts-iptv-hub.jks")
            storePassword = "bertsiptvhub"
            keyAlias = "bertsiptvhub"
            keyPassword = "bertsiptvhub"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("hub")
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
        debug {
            signingConfig = signingConfigs.getByName("hub")
        }
    }

    buildFeatures {
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    lint {
        abortOnError = false
        checkReleaseBuilds = false
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.recyclerview:recyclerview:1.3.2")
    implementation("androidx.media3:media3-exoplayer:1.5.1")
    implementation("androidx.media3:media3-ui:1.5.1")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("io.coil-kt:coil:2.7.0")
    // VLC's engine, built in: plays 4K/HEVC and other streams some boxes' ExoPlayer decoders reject.
    implementation("org.videolan.android:libvlc-all:3.6.0")
}
