import { paths, type Route } from '../route'

const items: { label: string; href: string; routes: Route['name'][] }[] = [
  { label: 'Classes', href: paths.classes, routes: ['classes'] },
  { label: 'Create Class', href: paths.createClass, routes: ['create-class'] },
  { label: 'Application', href: paths.applicationLookup, routes: ['application-lookup', 'application'] },
  { label: 'My Applications', href: paths.myApplications, routes: ['my-applications'] },
]

export default function Nav({ route }: { route: Route }) {
  return (
    <nav aria-label="Main">
      {items.map((item) => (
        <a key={item.href} href={item.href} aria-current={item.routes.includes(route.name) ? 'page' : undefined}>
          {item.label}
        </a>
      ))}
    </nav>
  )
}
