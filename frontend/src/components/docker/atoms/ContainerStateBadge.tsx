// Back-compat shim: the badge implementation now lives in the shared module so
// the Docker and Proxmox surfaces use literally the same component.
export { StateBadge as ContainerStateBadge, type StateBadgeProps as ContainerStateBadgeProps } from '@/components/shared/StateBadge'
