/**
 * Square AZOA mark. Decorative when the word AZOA sits beside it.
 * File: public/brand/azoa-logo.svg
 */
export function BrandMark({
  size = 28,
  className,
}: {
  size?: number
  className?: string
}) {
  return (
    // The mark is a static public SVG. next/image does not optimize SVG.
    // eslint-disable-next-line @next/next/no-img-element
    <img
      src="/brand/azoa-logo.svg"
      alt=""
      width={size}
      height={size}
      className={className}
    />
  )
}
