import ErrorBoundary from './components/ErrorBoundary'
import Nav from './components/Nav'
import StudentIdBar from './components/StudentIdBar'
import { useRoute, paths, type Route } from './route'
import ApplicationScreen, { ApplicationLookup } from './screens/ApplicationScreen'
import ClassListScreen from './screens/ClassListScreen'
import CreateClassScreen from './screens/CreateClassScreen'
import MyApplicationsScreen from './screens/MyApplicationsScreen'

function Screen({ route }: { route: Route }) {
  switch (route.name) {
    case 'classes':
      return <ClassListScreen />
    case 'create-class':
      return <CreateClassScreen />
    case 'application-lookup':
      return <ApplicationLookup />
    case 'application':
      return <ApplicationScreen holdId={route.holdId} />
    case 'my-applications':
      return <MyApplicationsScreen />
    case 'not-found':
      return (
        <section>
          <h1>Page not found</h1>
          <p>
            <a href={paths.classes}>Go to the class list</a>
          </p>
        </section>
      )
  }
}

export default function App() {
  const route = useRoute()
  return (
    <ErrorBoundary>
      <header>
        <StudentIdBar />
        <Nav route={route} />
      </header>
      <main>
        {/* a failing screen keeps the ID box and navigation usable; moving to another screen retries */}
        <ErrorBoundary resetKey={JSON.stringify(route)}>
          <Screen route={route} />
        </ErrorBoundary>
      </main>
    </ErrorBoundary>
  )
}
