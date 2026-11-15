import { RideAlongView } from '@/components/money/ride-along-view'

export default async function RideLinkPage({ params }: { params: Promise<{ linkId: string }> }) {
  const { linkId } = await params
  return <RideAlongView linkIdFromPath={linkId} />
}
