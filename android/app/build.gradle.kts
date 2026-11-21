import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.camstudio.pro"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.camstudio.pro"
        minSdk = 26
        targetSdk = 35
        versionCode = 11
        versionName = "1.2.0"
    }
    // Release signing: android/keystore.properties locally, or CSP_KEYSTORE_* environment variables in CI.
    val keyProps = Properties().apply {
        val f = rootProject.file("keystore.properties")
        if (f.exists()) f.inputStream().use { load(it) }
    }
    fun key(name: String, env: String): String? = keyProps.getProperty(name) ?: System.getenv(env)
    val storePath = key("storeFile", "CSP_KEYSTORE_FILE")

    signingConfigs {
        if (storePath != null) create("release") {
            storeFile = rootProject.file(storePath)
            storePassword = key("storePassword", "CSP_KEYSTORE_PASSWORD")
            keyAlias = key("keyAlias", "CSP_KEY_ALIAS")
            keyPassword = key("keyPassword", "CSP_KEY_PASSWORD")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            // Without a key the release output is unsigned (…-unsigned.apk) rather than failing every build.
            signingConfig = signingConfigs.findByName("release")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures { compose = true; buildConfig = true }
}

kotlin { jvmToolchain(17) }

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2025.09.00")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.core:core-ktx:1.16.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.9.2")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.2")
    implementation("androidx.lifecycle:lifecycle-process:2.9.2")
    implementation("androidx.datastore:datastore-preferences:1.1.7")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")

    val camerax = "1.4.2"
    implementation("androidx.camera:camera-core:$camerax")
    implementation("androidx.camera:camera-camera2:$camerax")
    implementation("androidx.camera:camera-lifecycle:$camerax")

    implementation("com.google.mlkit:segmentation-selfie:16.0.0-beta6")
    implementation("com.google.mlkit:face-detection:16.1.7")
    implementation("com.google.zxing:core:3.5.3")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}

