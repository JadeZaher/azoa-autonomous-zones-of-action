import './runtime'
import { createNodeNetwork } from '../msw/node-network'
import { installSliceNetwork } from './lifecycle'

/** Slice setup for the jsdom `slices` project; runs after `src/test/setup.ts` (jsdom stubs). */
installSliceNetwork(createNodeNetwork())
