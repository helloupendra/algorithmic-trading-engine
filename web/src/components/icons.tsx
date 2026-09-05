/**
 * Inline SVG icon set — 24px viewBox, 1.7px stroke. No icon-font or package
 * dependency; each icon is a plain component so tree-shaking keeps only what a
 * page uses.
 *
 * Size comes from the parent's CSS (`.panel__title svg { width: 15px }` and the
 * like). The 16px width/height here are only a floor for an icon used somewhere
 * that has no such rule: a presentation attribute loses to any CSS selector, so
 * every existing rule still wins. Without it an unsized icon stretches to fill
 * whatever box it lands in, which is a startling way to find out you forgot a
 * rule.
 */

import type { ReactNode, SVGProps } from 'react'

function Icon({ children, ...props }: SVGProps<SVGSVGElement> & { children: ReactNode }) {
  return (
    <svg
      viewBox="0 0 24 24"
      width="16"
      height="16"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      {...props}
    >
      {children}
    </svg>
  )
}

export const IconLogo = (p: SVGProps<SVGSVGElement>) => (
  <Icon strokeWidth="2.2" {...p}>
    <path d="M3 17l5-8 4 5 3-4 6 7" />
  </Icon>
)

export const IconDashboard = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <rect x="3" y="3" width="7.5" height="9" rx="1.5" />
    <rect x="13.5" y="3" width="7.5" height="5.5" rx="1.5" />
    <rect x="13.5" y="12" width="7.5" height="9" rx="1.5" />
    <rect x="3" y="15.5" width="7.5" height="5.5" rx="1.5" />
  </Icon>
)

export const IconDatabase = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <ellipse cx="12" cy="5.5" rx="8" ry="3" />
    <path d="M4 5.5V12c0 1.66 3.58 3 8 3s8-1.34 8-3V5.5" />
    <path d="M4 12v6.5c0 1.66 3.58 3 8 3s8-1.34 8-3V12" />
  </Icon>
)

export const IconPulse = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M2.5 12h4l2.5-7 5 14 2.5-7h5" />
  </Icon>
)

export const IconActivity = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <polyline points="22 12 18 12 15 21 9 3 6 12 2 12" />
  </Icon>
)

export const IconCandles = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M7 4v3M7 15v4" />
    <rect x="4.75" y="7" width="4.5" height="8" rx="1" />
    <path d="M17 6v2M17 17v2" />
    <rect x="14.75" y="8" width="4.5" height="9" rx="1" />
  </Icon>
)

export const IconLayers = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M12 3l9 4.5-9 4.5-9-4.5L12 3z" />
    <path d="M3 12l9 4.5 9-4.5" />
    <path d="M3 16.5L12 21l9-4.5" />
  </Icon>
)

export const IconBot = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <rect x="5" y="8" width="14" height="11" rx="2.5" />
    <path d="M12 8V4.5M9.5 4.5h5" />
    <circle cx="9" cy="13" r="1" fill="currentColor" stroke="none" />
    <circle cx="15" cy="13" r="1" fill="currentColor" stroke="none" />
    <path d="M9.5 16.5h5" />
  </Icon>
)

export const IconFlask = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M9.5 3h5M10.5 3v5.2L5 18a2 2 0 001.8 3h10.4A2 2 0 0019 18L13.5 8.2V3" />
    <path d="M7.5 14.5h9" />
  </Icon>
)

export const IconShield = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M12 3l7.5 3v5.5c0 4.6-3.2 8-7.5 9.5-4.3-1.5-7.5-4.9-7.5-9.5V6L12 3z" />
    <path d="M9 12l2 2 4-4.5" />
  </Icon>
)

export const IconUsers = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <circle cx="9" cy="8.5" r="3.2" />
    <path d="M3.5 19.5c.6-3.2 2.8-5 5.5-5s4.9 1.8 5.5 5" />
    <circle cx="16.8" cy="9.5" r="2.5" />
    <path d="M16.5 14.6c2.2.2 3.7 1.7 4.2 4.4" />
  </Icon>
)

export const IconPlug = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M9 7V3.5M15 7v-3.5" />
    <path d="M6.5 7h11v4a5.5 5.5 0 01-11 0V7z" />
    <path d="M12 16.5V21" />
  </Icon>
)

export const IconBell = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M18 10a6 6 0 10-12 0c0 5-2 6-2 6h16s-2-1-2-6" />
    <path d="M10 19.5a2.2 2.2 0 004 0" />
  </Icon>
)

export const IconServer = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <rect x="3" y="4" width="18" height="7" rx="1.5" />
    <rect x="3" y="13" width="18" height="7" rx="1.5" />
    <path d="M6.5 7.5h.01M6.5 16.5h.01" />
  </Icon>
)

export const IconSearch = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <circle cx="11" cy="11" r="6.5" />
    <path d="M20.5 20.5l-4.9-4.9" />
  </Icon>
)

export const IconRefresh = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M20 11a8 8 0 10-2.3 6.3" />
    <path d="M20 6.5V11h-4.5" />
  </Icon>
)

export const IconDownload = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M12 3.5v11M7.5 10.5L12 15l4.5-4.5" />
    <path d="M4 16.5v2.5A1.5 1.5 0 005.5 20.5h13a1.5 1.5 0 001.5-1.5v-2.5" />
  </Icon>
)

export const IconPlay = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M7 4.5l12 7.5-12 7.5v-15z" />
  </Icon>
)

export const IconStop = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <rect x="6" y="6" width="12" height="12" rx="2" />
  </Icon>
)

export const IconTrash = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M4 6.5h16M9.5 6.5V4.75A1.25 1.25 0 0110.75 3.5h2.5a1.25 1.25 0 011.25 1.25V6.5" />
    <path d="M6.5 6.5l.8 12.5a1.5 1.5 0 001.5 1.5h6.4a1.5 1.5 0 001.5-1.5l.8-12.5" />
    <path d="M10 10.5v5.5M14 10.5v5.5" />
  </Icon>
)

export const IconPlus = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M12 5v14M5 12h14" />
  </Icon>
)

export const IconArrowRight = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M4.5 12h15M13.5 6l6 6-6 6" />
  </Icon>
)

export const IconChevronRight = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M9 5.5l6.5 6.5L9 18.5" />
  </Icon>
)

export const IconClock = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <circle cx="12" cy="12" r="8.5" />
    <path d="M12 7.5V12l3 2" />
  </Icon>
)

export const IconGlobe = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <circle cx="12" cy="12" r="8.5" />
    <path d="M3.5 12h17M12 3.5c2.6 2.3 3.9 5.2 3.9 8.5s-1.3 6.2-3.9 8.5c-2.6-2.3-3.9-5.2-3.9-8.5s1.3-6.2 3.9-8.5z" />
  </Icon>
)

export const IconWarning = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M12 4L2.5 20h19L12 4z" />
    <path d="M12 10v4.5M12 17.2v.3" />
  </Icon>
)

export const IconSignOut = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M9.5 3.5H5A1.5 1.5 0 003.5 5v14A1.5 1.5 0 005 20.5h4.5" />
    <path d="M15 8l4.5 4-4.5 4M19 12H9" />
  </Icon>
)

export const IconSwitch = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M16 4l4 4-4 4M20 8H7" />
    <path d="M8 20l-4-4 4-4M4 16h13" />
  </Icon>
)

export const IconChevronDown = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M5.5 9l6.5 6.5L18.5 9" />
  </Icon>
)

export const IconChevronUp = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M18.5 15.5l-6.5-6.5-6.5 6.5" />
  </Icon>
)

export const IconX = (p: SVGProps<SVGSVGElement>) => (
  <Icon {...p}>
    <path d="M6 6l12 12M18 6L6 18" />
  </Icon>
)

