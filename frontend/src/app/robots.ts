import type { MetadataRoute } from 'next'
import { PRIVATE_PREFIXES, SITE } from '@/content/public-documents'

export default function robots(): MetadataRoute.Robots {
  return {
    rules: {
      userAgent: '*',
      allow: '/',
      disallow: [...PRIVATE_PREFIXES],
    },
    sitemap: `${SITE}/sitemap.xml`,
    host: SITE,
  }
}
