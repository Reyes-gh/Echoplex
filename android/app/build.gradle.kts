plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// La versión es la misma que la de Echoplex del PC (<Version> de Echoplex.csproj, o -PechoplexVersion=1.2.3).
val echoplexVersion: String = providers.gradleProperty("echoplexVersion").orNull
    ?: rootDir.resolve("../Echoplex.csproj").takeIf { it.exists() }?.readText()
        ?.let { Regex("<Version>([^<]+)</Version>").find(it)?.groupValues?.get(1) }
    ?: "0.0.1"
// 1.2.3 -> 10203 (cada versión sube el número, que es lo que Android mira para actualizar)
val echoplexVersionCode = echoplexVersion.split('.').map { it.toIntOrNull() ?: 0 }.let { (it.getOrElse(0) { 0 } * 100 + it.getOrElse(1) { 0 }) * 100 + it.getOrElse(2) { 0 } }

// La clave de firma nunca va en el repo: echoplexStoreFile y echoplexStorePassword se leen de las propiedades de Gradle
// (gradle.properties de GRADLE_USER_HOME en el PC, variables ORG_GRADLE_PROJECT_* en GitHub). Sin ellas, clave de pruebas.
val storeFilePath: String? = providers.gradleProperty("echoplexStoreFile").orNull
val storePass: String? = providers.gradleProperty("echoplexStorePassword").orNull

android {
    namespace = "io.github.reyesgh.echoplex"
    compileSdk = 35

    defaultConfig {
        applicationId = "io.github.reyesgh.echoplex"
        minSdk = 26
        targetSdk = 35
        versionCode = echoplexVersionCode
        versionName = echoplexVersion
    }

    signingConfigs {
        if (storeFilePath != null) {
            require(!storePass.isNullOrEmpty()) { "Falta echoplexStorePassword para la clave $storeFilePath" }
            create("release") {
                storeFile = file(storeFilePath)
                storePassword = storePass
                keyAlias = providers.gradleProperty("echoplexKeyAlias").orNull ?: "echoplex"
                keyPassword = storePass
            }
        }
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        compose = true
    }
    // libechoplexts.so (el Tailscale de la app) es un programa que se ejecuta: tiene que quedar extraído en disco
    packaging {
        jniLibs { useLegacyPackaging = true }
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime)
    implementation(libs.androidx.lifecycle.viewmodel)
    implementation(libs.androidx.navigation.compose)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons)

    implementation(libs.media3.exoplayer)
    implementation(libs.media3.session)
    implementation(libs.media3.datasource.okhttp)

    implementation(libs.coil.compose)
    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)

    implementation(libs.camerax.camera2)
    implementation(libs.camerax.lifecycle)
    implementation(libs.camerax.view)
    implementation(libs.zxing.core)

    implementation(libs.datastore.preferences)
    implementation(libs.palette)
    // perfiles de las librerías (Compose…) compilados al instalar: listas y pantallas fluidas desde el primer uso
    implementation(libs.androidx.profileinstaller)
}
