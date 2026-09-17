import java.util.Properties

/**
 * Release signing material lives outside both repositories -- this one is public, and an Android
 * signing key is the only thing standing between a user and an app that claims to be this one.
 * Without it the release build is simply unsigned, which is what any machine that is not the
 * maintainer's should get.
 */
val signingProperties = Properties().apply {
    val path = System.getenv("EVEINTEL_SIGNING")
        ?: "${System.getProperty("user.home")}/.eveintel/signing.properties"
    val file = File(path)
    if (file.isFile) file.inputStream().use { load(it) }
}

plugins {
    // AGP 9 provides Kotlin support itself; applying `kotlin.android` alongside it is an error.
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.kotlin.compose)
}

android {
    namespace = "dev.eveintel.android"
    compileSdk = 36

    defaultConfig {
        applicationId = "dev.eveintel.android"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }

    signingConfigs {
        if (signingProperties.getProperty("storeFile") != null) {
            create("release") {
                storeFile = File(signingProperties.getProperty("storeFile"))
                storePassword = signingProperties.getProperty("storePassword")
                keyAlias = signingProperties.getProperty("keyAlias")
                keyPassword = signingProperties.getProperty("keyPassword")
                // v3 carries the rotation record, so this key can be replaced later without
                // orphaning everyone who already installed. v1 is dead weight above API 24.
                enableV1Signing = false
                enableV2Signing = true
                enableV3Signing = true
            }
        }
    }

    buildFeatures {
        compose = true
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release")
        }
    }

    // Tablets are landscape-first; the feed and map both assume width.
    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_11)
    }
}

// Coil pulls Compose UI 1.12 transitively, which refuses to build against anything below
// compileSdk 37 — and android-37 is not in the SDK repository yet. Hold the whole Compose runtime
// at the last 1.11 release so the transitive bump cannot reintroduce that requirement.
configurations.configureEach {
    resolutionStrategy.eachDependency {
        if (requested.group.startsWith("androidx.compose.") &&
            requested.group != "androidx.compose.material3" &&
            requested.group != "androidx.compose.material"
        ) {
            useVersion(libs.versions.compose.ui.get())
        }
    }
}

dependencies {
    implementation(project(":shared"))

    implementation(libs.kotlinx.coroutines.core)
    implementation(libs.kotlinx.serialization.json)

    implementation(libs.ktor.client.core)
    implementation(libs.ktor.client.okhttp)
    implementation(libs.ktor.client.websockets)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.service)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)

    implementation(libs.coil.compose)
    implementation(libs.coil.network.okhttp)

    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons.extended)
    debugImplementation(libs.compose.ui.tooling)
}
