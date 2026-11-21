import java.security.MessageDigest

plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}
android {
    namespace = "com.camstudio.chairseg"
    compileSdk = 36
    defaultConfig { minSdk = 26; testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner" }
    sourceSets["androidTest"].assets.srcDir("../app/src/main/assets/backgrounds")
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
kotlin { jvmToolchain(17) }
dependencies {
    implementation("com.google.mediapipe:tasks-vision:1.2.3")
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
val verifyChairModel by tasks.registering {
    val model=layout.projectDirectory.file("src/main/assets/interactive_segmentation.task")
    inputs.file(model)
    doLast {
        val sha=MessageDigest.getInstance("SHA-256").digest(model.asFile.readBytes())
            .joinToString("") { "%02x".format(it) }
        check(sha=="38431bc66b883404e8397f74c3579404315b9b52b04a46c6346fe906a7309b03") { "Unexpected MediaPipe model SHA256: $sha" }
    }
}
tasks.named("preBuild") { dependsOn(verifyChairModel) }
