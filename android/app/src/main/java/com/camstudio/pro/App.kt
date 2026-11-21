package com.camstudio.pro

import android.app.Application

/** Holds the single [Engine] so streaming survives activity recreation. */
class App : Application() {
    lateinit var engine: Engine
        private set

    override fun onCreate() {
        super.onCreate()
        engine = Engine(this)
    }
}
