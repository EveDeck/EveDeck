plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.serialization)
    application
}

dependencies {
    implementation(project(":shared"))
    implementation(libs.kotlinx.coroutines.core)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.ktor.server.core)
    implementation(libs.ktor.server.cio)
    implementation(libs.ktor.server.websockets)

    // ESI lookups and the image proxy
    implementation(libs.ktor.client.core)
    implementation(libs.ktor.client.cio)
    implementation(libs.ktor.client.content.negotiation)
    implementation(libs.ktor.serialization.kotlinx.json)
    implementation(libs.slf4j.simple)

    // The tray and settings window. Swing's own look and feel cannot be made to match EveDeck
    // without rewriting every component's painter; FlatLaf is a theming layer over the same
    // components, so the window stays plain Swing and only its colours change.
    implementation(libs.flatlaf)

    testImplementation(kotlin("test"))
}

application {
    mainClass.set("dev.eveintel.daemon.MainKt")
}
