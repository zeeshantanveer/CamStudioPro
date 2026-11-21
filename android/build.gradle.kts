plugins {
    id("com.android.application") version "8.13.1" apply false
    id("com.android.library") version "8.13.1" apply false
    id("org.jetbrains.kotlin.android") version "2.2.21" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.2.21" apply false
}

// Optional: put build output on another drive, e.g. CSP_BUILD_ROOT=C:\builds\camstudio
System.getenv("CSP_BUILD_ROOT")?.takeIf { it.isNotBlank() }?.let { root ->
    allprojects { layout.buildDirectory.set(file("$root/${project.name}")) }
}
