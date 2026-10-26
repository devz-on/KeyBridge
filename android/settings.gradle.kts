pluginManagement {
    repositories { google(); mavenCentral(); gradlePluginPortal() }
    resolutionStrategy {
        eachPlugin {
            if (requested.id.id in setOf("org.jetbrains.kotlin.jvm", "org.jetbrains.kotlin.android"))
                useModule("org.jetbrains.kotlin:kotlin-gradle-plugin:${requested.version}")
        }
    }
}
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories { google(); mavenCentral() }
}
rootProject.name = "KeyBridge"
include(":core", ":app")
