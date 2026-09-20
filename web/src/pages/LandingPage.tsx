/**
 * Public homepage ("/").
 *
 * Two views of one story, chosen by the screen it is opened on (lib/sceneMode):
 * a phone gets landing/Mobile — composed for a tall touch screen, the engine on
 * top and the copy in a sheet under the thumb — and a tablet or a computer gets
 * landing/Desktop. They share their words, figures and screens
 * (landing/content), so they cannot disagree; turning a phone or resizing a
 * window swaps the view rather than stretching the wrong one.
 */

import { usePhone } from '../lib/sceneMode'
import { LandingDesktop } from './landing/Desktop'
import { LandingMobile } from './landing/Mobile'

export function LandingPage() {
  return usePhone() ? <LandingMobile /> : <LandingDesktop />
}
