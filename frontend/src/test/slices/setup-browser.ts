import './browser-process-shim'
import './runtime'
import '@testing-library/jest-dom/vitest'
import { createBrowserNetwork } from '../msw/browser-network'
import { installSliceNetwork } from './lifecycle'

/** Slice setup for the Chromium `slices-browser` project: real layout, no jsdom stubs. */
installSliceNetwork(createBrowserNetwork())
