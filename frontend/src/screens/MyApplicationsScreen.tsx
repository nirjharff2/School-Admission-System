import StudentGate from '../components/StudentGate'

export default function MyApplicationsScreen() {
  return (
    <section>
      <h1>My Applications</h1>
      <StudentGate>
        <p className="hint">The list of this student's applications is added in a later step.</p>
      </StudentGate>
    </section>
  )
}
