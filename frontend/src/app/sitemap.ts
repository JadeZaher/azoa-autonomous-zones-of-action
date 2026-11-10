import type { MetadataRoute } from 'next'
import { absoluteUrl, sitemapDocuments } from '@/content/public-documents'

export default function sitemap(): MetadataRoute.Sitemap {
  return sitemapDocuments().map((document) => ({
    url: absoluteUrl(document.path),
    ...(document.publishedAt ? { lastModified: document.publishedAt } : {}),
  }))
}
