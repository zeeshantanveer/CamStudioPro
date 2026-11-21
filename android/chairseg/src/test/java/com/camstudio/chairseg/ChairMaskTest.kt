package com.camstudio.chairseg

import org.junit.Assert.*
import org.junit.Test
import kotlin.random.Random

class ChairMaskTest {
    @Test fun noChairSelectionCanCutHolesInPersonMask() {
        val r = Random(42)
        repeat(10000) {
            val person = r.nextFloat(); val chair = r.nextFloat()
            assertTrue(ChairMask.union(person,chair) >= person)
        }
    }
    @Test fun erasingChairNeverErasesUnderlyingPerson() {
        val chair = ChairMask(20,20,FloatArray(400) { 1f })
        chair.brush(.5f,.5f,.3f,false)
        assertEquals(0f,chair.sample(.5f,.5f),0f)
        assertEquals(.9f,ChairMask.union(.9f,chair.sample(.5f,.5f)),0f)
        assertEquals(1f,chair.sample(.05f,.05f),0f)
    }
    @Test fun letterboxTapsDoNotSelectTheWrongImageLocation() {
        assertNull(ChairMask.point(50f,10f,100,100,200,100))
        assertEquals(.5f,ChairMask.point(50f,50f,100,100,200,100)!!.second,.001f)
        assertEquals(.1f,ChairMask.point(10f,50f,100,100,200,100)!!.first,.001f)
    }
}
