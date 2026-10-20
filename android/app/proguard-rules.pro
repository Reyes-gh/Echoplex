# Las respuestas de Subsonic se leen con kotlinx.serialization: sus clases se conservan
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keep,includedescriptorclasses class io.github.reyesgh.echoplex.**$$serializer { *; }
-keepclassmembers class io.github.reyesgh.echoplex.** {
    *** Companion;
}
-keepclasseswithmembers class io.github.reyesgh.echoplex.** {
    kotlinx.serialization.KSerializer serializer(...);
}
