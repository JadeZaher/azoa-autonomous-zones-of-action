import { buildFeedXml } from '@/content/public-documents'

export function GET() {
  return new Response(buildFeedXml(), {
    headers: {
      'Content-Type': 'application/rss+xml; charset=utf-8',
      'Cache-Control': 'public, max-age=3600',
    },
  })
}
