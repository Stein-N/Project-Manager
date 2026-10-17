plugins {
    id("java")
    id("application")
}

group = "net.morthen"

repositories {
    mavenCentral()
}

val commonmarkVersion = "0.30.1"
val javafxVersion = "27"

// JavaFX ships native code per platform. The jar bundles several platforms so one file runs everywhere
// (JavaFX picks the right native libraries at runtime). Override with e.g. -PjavafxPlatforms=win,linux,mac-aarch64
val javafxPlatforms = (findProperty("javafxPlatforms") as String? ?: "win,linux")
    .split(",").map { it.trim() }.filter { it.isNotEmpty() }

dependencies {
    implementation("com.formdev:flatlaf:3.6.1")
    implementation("com.google.code.gson:gson:2.11.0")

    // Markdown with the GitHub extensions
    implementation("org.commonmark:commonmark:$commonmarkVersion")
    for (ext in listOf("gfm-tables", "gfm-strikethrough", "task-list-items", "autolink", "heading-anchor", "footnotes")) {
        implementation("org.commonmark:commonmark-ext-$ext:$commonmarkVersion")
    }

    // JavaFX WebView (embedded into Swing via JFXPanel) renders the README
    for (platform in javafxPlatforms) {
        for (module in listOf("base", "graphics", "controls", "media", "web", "swing")) {
            implementation("org.openjfx:javafx-$module:$javafxVersion:$platform")
        }
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
        attributes["Implementation-Title"] = "MC Mod Project Manager"
        attributes["Implementation-Version"] = project.version
        attributes["Enable-Native-Access"] = "ALL-UNNAMED"
    }
    duplicatesStrategy = DuplicatesStrategy.EXCLUDE
    from({ configurations.runtimeClasspath.get().map { zipTree(it) } })
    exclude("module-info.class", "META-INF/versions/*/module-info.class")
}

tasks.test {
    useJUnitPlatform()
}
