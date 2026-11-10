import { notFound, permanentRedirect } from 'next/navigation'
import { essayBySlug, essayPath } from '@/content/public-documents'

export default async function BlogEssayRedirect({ params }: { params: Promise<{ slug: string }> }) {
  const { slug } = await params
  if (!essayBySlug(slug)) notFound()
  permanentRedirect(essayPath(slug))
}
