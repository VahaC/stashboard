import { describe, it, expect } from 'vitest'
import { render } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { ContainerCard } from './ContainerCard'
import type { DockerContainerCard } from '@/lib/types'

function makeCard(overrides: Partial<DockerContainerCard> = {}): DockerContainerCard {
  return {
    id: 'abc',
    name: 'jellyfin',
    image: 'lscr.io/linuxserver/jellyfin:latest',
    imageId: 'sha256:cafe',
    state: 'running',
    status: 'Up 2 hours',
    createdUtc: null,
    ports: [],
    composeProject: null,
    composeService: null,
    watchId: null,
    webResourceId: null,
    iconDataUri: null,
    proxmoxLink: null,
    ...overrides,
  }
}

function renderCard(card: DockerContainerCard) {
  return render(
    <MemoryRouter>
      <ContainerCard
        card={card}
        variant="docker-page"
        allowRemoval={false}
        isSshHost={false}
        busy={false}
        onOpen={() => {}}
        onAction={() => {}}
      />
    </MemoryRouter>,
  )
}

describe('ContainerCard icon', () => {
  it('renders a custom data-URI avatar', () => {
    const { container } = renderCard(makeCard({ iconDataUri: 'data:image/png;base64,CUSTOM' }))
    const img = container.querySelector('.cc-card-icon-img') as HTMLImageElement | null
    expect(img).not.toBeNull()
    expect(img!.getAttribute('src')).toBe('data:image/png;base64,CUSTOM')
  })

  it('renders an official data-URI avatar', () => {
    const { container } = renderCard(makeCard({ iconDataUri: 'data:image/webp;base64,OFFICIAL' }))
    const img = container.querySelector('.cc-card-icon-img') as HTMLImageElement | null
    expect(img!.getAttribute('src')).toBe('data:image/webp;base64,OFFICIAL')
  })

  it('renders the placeholder (initials, no image) when iconDataUri is null', () => {
    const { container } = renderCard(makeCard({ name: 'jellyfin', iconDataUri: null }))
    expect(container.querySelector('.cc-card-icon-img')).toBeNull()
    const fallback = container.querySelector('.cc-card-icon-fallback')
    expect(fallback).not.toBeNull()
    expect(fallback!.textContent).toBe('JE')
  })
})
