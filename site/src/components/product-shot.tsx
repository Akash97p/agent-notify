import Image from "next/image";

export function ProductShot({ src, alt, caption, priority = false }: { src: string; alt: string; caption?: string; priority?: boolean }) {
  return (
    <figure className="min-w-0">
      <div className="overflow-hidden rounded-xl border bg-card shadow-2xl shadow-black">
        <Image src={src} alt={alt} width={1200} height={720} className="h-auto w-full" priority={priority} unoptimized />
      </div>
      {caption ? <figcaption className="mt-3 text-xs leading-5 text-muted-foreground">{caption}</figcaption> : null}
    </figure>
  );
}
