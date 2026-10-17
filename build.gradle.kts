plugins {
    id("java")
    id("application")
}

group = "net.morthen"
version = "1.0-SNAPSHOT"

repositories {
    mavenCentral()
}

val commonmarkVersion = "0.30.1"
val javafxVersion = "27"

// JavaFX ships native code per platform
val javafxPlatform = System.getProperty("os.name").lowercase().let { os ->
    val arm = System.getProperty("os.arch").lowercase().contains("aarch64")
    when {
        os.contains("win") -> "win"
        os.contains("mac") -> if (arm) "mac-aarch64" else "mac"
        else -> if (arm) "linux-aarch64" else "linux"
    }
}

dependencies {
    implementation("com.formdev:flatlaf:3.6.1")
    implementation("com.google.code.gson:gson:2.11.0")

    // Markdown with the GitHub extensions
    implementation("org.commonmark:commonmark:$commonmarkVersion")
    for (ext in listOf("gfm-tables", "gfm-strikethrough", "task-list-items", "autolink", "heading-anchor", "footnotes")) {
        implementation("org.commonmark:commonmark-ext-$ext:$commonmarkVersion")
    }

    // JavaFX WebView (embedded into Swing via JFXPanel) renders the README
    for (module in listOf("base", "graphics", "controls", "media", "web", "swing")) {
        implementation("org.openjfx:javafx-$module:$javafxVersion:$javafxPlatform")
    }

    testImplementation(platform("org.junit:junit-bom:6.0.0"))
    testImplementation("org.junit.jupiter:junit-jupiter")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

application {
    mainClass = "net.morthen.Main"
    applicationDefaultJvmArgs = listOf("--enable-native-access=ALL-UNNAMED")
}

// Fat jar: runnable via double-click or "javaw -jar"
tasks.jar {
    manifest {
        attributes["Main-Class"] = "net.morthen.Main"
        attributes["Enable-Native-Access"] = "ALL-UNNAMED"
    }
    duplicatesStrategy = DuplicatesStrategy.EXCLUDE
    from({ configurations.runtimeClasspath.get().map { zipTree(it) } })
    exclude("module-info.class", "META-INF/versions/*/module-info.class")
}

tasks.test {
    useJUnitPlatform()
}
