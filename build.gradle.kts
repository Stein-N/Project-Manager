plugins {
    id("java")
    id("application")
}

group = "net.morthen"
version = "1.0-SNAPSHOT"

repositories {
    mavenCentral()
}

dependencies {
    implementation("com.formdev:flatlaf:3.6.1")
    implementation("com.google.code.gson:gson:2.11.0")
    implementation("org.commonmark:commonmark:0.24.0")
    implementation("org.commonmark:commonmark-ext-gfm-tables:0.24.0")

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
}

tasks.test {
    useJUnitPlatform()
}
