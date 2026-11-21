# CamStudio Pro release rules. Library consumer rules (CameraX, ML Kit, Compose) are applied automatically.

# Keep line numbers so release crash stack traces are readable (with the mapping file).
-keepattributes SourceFile,LineNumberTable
-renamesourcefileattribute SourceFile

# ZXing core uses no reflection, but silence warnings for optional desktop classes.
-dontwarn com.google.zxing.**

# org.json is part of the platform.
-dontwarn org.json.**
